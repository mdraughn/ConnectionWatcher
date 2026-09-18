using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Network;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ConEngine;

/// <summary>
/// Orchestrates the Phase 1 engine: builds the target graph from configuration,
/// wires monitors, disciplines, topology and pool coordinators, and routes
/// ping results from the PingManager to the right monitor.
/// </summary>
public class WatcherEngine
{
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingFactory;
    private readonly IDomainNameResolverFactory _resolverFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatcherEngine> _log;
    private readonly WatcherOptions _options;

    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly List<PoolCoordinator> _poolCoordinators = new();
    private readonly List<IDisposable> _subscriptions = new();
    /// <summary>Per hostname target: completing the source wakes its DNS refresh loop early.</summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _dnsRefreshRequests = new();
    private readonly Subject<TargetTransition> _transitionSubject = new();
    private readonly Subject<(Guid TargetId, PingResult Result)> _pingSampleSubject = new();

    private TargetGraph? _graph;
    private PingManager? _manager;
    private TopologyCoordinator? _topology;
    private CancellationTokenSource? _cts;

    private readonly VerdictOptions _verdictOptions;
    private readonly TimeSpan _dnsRefreshInterval;

    /// <summary>Default re-resolution period for hostname targets. There is
    /// no TTL to honor (System.Net.Dns hides it); five minutes tracks CDN-scale
    /// record changes without hammering the resolver.</summary>
    public static readonly TimeSpan DefaultDnsRefreshInterval = TimeSpan.FromMinutes(5);

    public WatcherEngine(TimeProvider timeProvider, IPingTransceiverFactory pingFactory,
        IDomainNameResolverFactory resolverFactory, ILoggerFactory loggerFactory,
        IOptions<WatcherOptions> options, IOptions<VerdictOptions>? verdictOptions = null,
        TimeSpan? dnsRefreshInterval = null)
    {
        _timeProvider = timeProvider;
        _pingFactory = pingFactory;
        _resolverFactory = resolverFactory;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _verdictOptions = verdictOptions?.Value ?? new VerdictOptions();
        _dnsRefreshInterval = dnsRefreshInterval ?? DefaultDnsRefreshInterval;
        _log = loggerFactory.CreateLogger<WatcherEngine>();
    }

    /// <summary>The verdict layer, or null when no anchor pool is configured.</summary>
    public VerdictCoordinator? Verdict { get; private set; }

    /// <summary>Suspicion bursts, corroboration sweeps, and glitch episodes.</summary>
    public SuspicionCoordinator? Suspicion { get; private set; }

    /// <summary>Sub-Faltering loss episodes worth recording.</summary>
    public IObservable<GlitchEvent> GlitchSource => _glitchSubject;
    private readonly System.Reactive.Subjects.Subject<GlitchEvent> _glitchSubject = new();

    /// <summary>Merged transition stream: every monitor and every pool.</summary>
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

    /// <summary>Every routed ping result, tagged with its target id (feeds rollups).</summary>
    public IObservable<(Guid TargetId, PingResult Result)> PingSampleSource => _pingSampleSubject;
    public IReadOnlyDictionary<Guid, TargetMonitor> Monitors => _monitors;
    public IReadOnlyList<PoolCoordinator> PoolCoordinators => _poolCoordinators;
    public TargetGraph Graph => _graph ?? throw new InvalidOperationException("Engine not started");

    private bool _warmStart;

    /// <summary>
    /// Starts the engine, optionally warm: <paramref name="seed"/> carries
    /// presumed subject states (from the last recorded transitions or a
    /// predecessor engine) that are adopted silently, shown as assumed, and
    /// burst-confirmed by fresh pings. A target seed applies only when its
    /// address still matches configuration.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken, EngineSeed? seed = null)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;
        _warmStart = seed is not null && !seed.IsEmpty;

        _graph = TargetGraph.FromOptions(_options);
        _log.LogInformation("WatcherEngine starting: {TargetCount} targets, {PoolCount} pools",
            _graph.Targets.Count, _graph.Pools.Count);

        // Pool membership index for phase staggering: targetId -> (memberIndex, memberCount).
        var poolPosition = new Dictionary<Guid, (int index, int count)>();
        foreach (var pool in _graph.Pools.Values)
            for (int i = 0; i < pool.MemberTargetIds.Count; i++)
                poolPosition[pool.MemberTargetIds[i]] = (i, pool.MemberTargetIds.Count);

        // Monitors and disciplines for enabled targets.
        foreach (var target in _graph.Targets.Values.Where(t => t.Enabled))
        {
            var profile = _graph.ProfileFor(target);
            var phaseOffset = poolPosition.TryGetValue(target.Id, out var pos)
                ? PoolCoordinator.PhaseOffset(pos.index, pos.count, profile.NormalInterval)
                : TimeSpan.Zero;
            var discipline = new PingDiscipline(profile, _timeProvider, phaseOffset);
            var monitor = new TargetMonitor(target, profile, discipline, _timeProvider, _loggerFactory);
            if (seed?.Targets.TryGetValue(target.Id, out var targetSeed) == true &&
                targetSeed.Address == target.Address &&
                targetSeed.Probe == target.Probe)
            {
                monitor.Seed(targetSeed.State);
                _log.LogInformation("Warm start: {Target} presumed {State}",
                    target.Name, targetSeed.State);
            }
            _monitors[target.Id] = monitor;
            _subscriptions.Add(monitor.TransitionSource.Subscribe(_transitionSubject.OnNext));
        }

        // Topology enforcement.
        _topology = new TopologyCoordinator(_graph, _monitors,
            _loggerFactory.CreateLogger<TopologyCoordinator>());
        _topology.Start();

        // Pool aggregation (members that are disabled have no monitor and are skipped).
        foreach (var pool in _graph.Pools.Values)
        {
            var members = pool.MemberTargetIds
                .Where(_monitors.ContainsKey)
                .Select(id => _monitors[id])
                .ToList();
            if (members.Count == 0)
                continue;
            var coordinator = new PoolCoordinator(pool, members, _timeProvider,
                _loggerFactory.CreateLogger<PoolCoordinator>());
            if (seed?.Pools.TryGetValue(pool.Id, out var poolSeed) == true)
                coordinator.Seed(poolSeed);
            _subscriptions.Add(coordinator.TransitionSource.Subscribe(_transitionSubject.OnNext));
            coordinator.Start();
            _poolCoordinators.Add(coordinator);
        }

        // The anchor pool, when configured and present, feeds both the verdict
        // and the suspicion layers.
        PoolDefinition? anchorPool = null;
        if (_verdictOptions.AnchorPool is { Length: > 0 } anchorPoolName)
        {
            anchorPool = _graph.Pools.Values.FirstOrDefault(p =>
                p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase));
            if (anchorPool is null)
                _log.LogWarning("Verdict disabled: anchor pool '{Pool}' not found", anchorPoolName);
        }

        // Verdict layer, if an anchor pool is configured.
        if (anchorPool is not null)
        {
            Verdict = new VerdictCoordinator(_graph, anchorPool, _monitors,
                _verdictOptions.WeatherThreshold, _timeProvider,
                _loggerFactory.CreateLogger<VerdictCoordinator>());
            if (seed?.Verdict is { } verdictSeed)
                Verdict.Seed(verdictSeed);
            _subscriptions.Add(Verdict.TransitionSource.Subscribe(_transitionSubject.OnNext));
            Verdict.Start();
        }

        // Suspicion layer: works without an anchor pool (per-target glitches
        // only); sweeps need the pool.
        Suspicion = new SuspicionCoordinator(_graph, anchorPool, _monitors,
            _verdictOptions.WeatherThreshold, _timeProvider,
            _loggerFactory.CreateLogger<SuspicionCoordinator>());
        _subscriptions.Add(Suspicion.GlitchSource.Subscribe(_glitchSubject.OnNext));

        // Ping plumbing: one manager, one controller per target, results
        // routed by target id (targets may share an address).
        _manager = new PingManager(_pingFactory, ct, _loggerFactory);
        _subscriptions.Add(_manager.PingResultSource.Subscribe(
            routed =>
            {
                if (_monitors.TryGetValue(routed.TargetId, out var monitor))
                {
                    var result = routed.Result;
                    monitor.Discipline.OnPingResult(result.Success);
                    monitor.RecordPingResult(result);
                    _pingSampleSubject.OnNext((monitor.Target.Id, result));
                    Suspicion?.OnPingSample(monitor.Target.Id, result);
                }
            },
            ex => _log.LogError(ex, "Error in ping result stream"),
            () => _log.LogInformation("Ping result stream completed")));

        // A hostname target that goes Down while the trunk is fine has most
        // likely had its address retired: re-resolve now, not at the next tick.
        _subscriptions.Add(_transitionSubject.Subscribe(t =>
        {
            if (t.Kind == TransitionSubjectKind.Target && t.To == HostState.Down &&
                _dnsRefreshRequests.TryGetValue(t.TargetId, out var request))
                request.TrySetResult();
        }));

        // Activate every enabled target (immediately for IP literals, via DNS otherwise).
        foreach (var (targetId, monitor) in _monitors)
        {
            var target = _graph.Targets[targetId];
            if (IPAddress.TryParse(target.Address, out var ip))
                Activate(monitor, ip);
            else
                _ = Task.Run(() => ResolveAndActivateAsync(monitor, target.Address, ct), ct);
        }

        _log.LogInformation("WatcherEngine started with {MonitorCount} active monitors", _monitors.Count);
        return Task.CompletedTask;
    }

    private void Activate(TargetMonitor monitor, IPAddress ip)
    {
        monitor.StartTracking(ip);
        StartController(monitor, ip);
        // Warm starts burst everything so presumed states confirm in seconds
        // (seeded Faltering/Down already burst via the discipline; this covers
        // Responding/Starting, and no-ops on parked or already-bursting ones).
        if (_warmStart)
            monitor.Discipline.TriggerBurst();
        _log.LogInformation("Activated {Target} at {Ip}", monitor.Target.Name, ip);
    }

    private void StartController(TargetMonitor monitor, IPAddress ip)
    {
        // Bursts shorten the timeout: replies arrive in well under a second,
        // and against a dead host the steady timeout dominates burst cost.
        var steadyTimeoutMs = (int)monitor.Profile.PingTimeout.TotalMilliseconds;
        ProbeSpec.TryParse(monitor.Target.Probe, out var probe);
        // Bursts shorten the timeout — but a refused TCP connect takes ~2s to
        // surface on Windows (the stack retries after the RST), so TCP probes
        // keep enough budget to classify Refused as a reply during bursts.
        var burstCapMs = probe.Kind == ProbeKind.Tcp ? 2500 : 1000;
        _manager!.GetOrCreateController(monitor.Target.Id, ip, probe, monitor.Discipline,
            () => monitor.Discipline.IsBurstActive ? Math.Min(steadyTimeoutMs, burstCapMs) : steadyTimeoutMs);
    }

    /// <summary>DNS moved the target: swap the controller, keep the monitor's
    /// state and history, and burst so the new address proves itself fast.</summary>
    private async Task RepointAsync(TargetMonitor monitor, IPAddress ip)
    {
        await _manager!.StopControllerAsync(monitor.Target.Id, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        monitor.Repoint(ip);
        StartController(monitor, ip);
        monitor.Discipline.TriggerBurst();
    }

    /// <summary>
    /// Owns a hostname target for the engine's lifetime: resolves (with backoff
    /// until the first answer), activates, then keeps re-resolving every
    /// <see cref="_dnsRefreshInterval"/> and immediately when the target goes
    /// Down. The current address is kept while it is still in the answer set;
    /// when it drops out the target is re-pointed at the first address. A
    /// failed refresh keeps the current address.
    /// </summary>
    private async Task ResolveAndActivateAsync(TargetMonitor monitor, string hostname, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(30);
        var maxBackoff = TimeSpan.FromMinutes(5);
        using var resolver = _resolverFactory.Create(ct);
        IPAddress? current = null;

        while (!ct.IsCancellationRequested)
        {
            List<IPAddress> answers = [];
            try
            {
                var result = await resolver.ResolveAsync(hostname, 5000).ConfigureAwait(false);
                answers = result.IPv4Addresses.ToList();
                if (current is null)
                {
                    if (!result.Success)
                        monitor.ReportResolutionFailure($"DNS lookup failed for '{hostname}': {result.Status}");
                    else if (answers.Count == 0)
                        monitor.ReportResolutionFailure($"No IPv4 address for '{hostname}'");
                }
                else if (!result.Success)
                {
                    _log.LogDebug("DNS refresh failed for {Hostname} ({Status}); keeping {Ip}",
                        hostname, result.Status, current);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (current is null)
                {
                    _log.LogWarning(ex, "DNS resolution failed for {Hostname}", hostname);
                    monitor.ReportResolutionFailure($"DNS lookup failed for '{hostname}': {ex.Message}");
                }
                else
                {
                    _log.LogDebug(ex, "DNS refresh failed for {Hostname}; keeping {Ip}", hostname, current);
                }
            }

            if (current is null)
            {
                if (answers.Count == 0)
                {
                    // Not activated yet: retry with backoff until DNS answers.
                    try { await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
                    continue;
                }
                current = answers[0];
                Activate(monitor, current);
            }
            else if (answers.Count > 0 && !answers.Contains(current))
            {
                _log.LogInformation("{Target}: {Old} left the DNS answer set; re-pointing to {New}",
                    monitor.Target.Name, current, answers[0]);
                current = answers[0];
                await RepointAsync(monitor, current).ConfigureAwait(false);
            }

            // Sleep until the refresh interval or a Down transition, whichever first.
            var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _dnsRefreshRequests[monitor.Target.Id] = request;
            try
            {
                await Task.WhenAny(Task.Delay(_dnsRefreshInterval, _timeProvider, ct), request.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task StopAsync()
    {
        _log.LogInformation("WatcherEngine stopping...");
        _cts?.Cancel();

        foreach (var s in _subscriptions)
            s.Dispose();
        _subscriptions.Clear();

        _topology?.Dispose();
        Verdict?.Dispose();
        Suspicion?.Dispose();
        foreach (var pool in _poolCoordinators)
            pool.Dispose();
        if (_manager is { } manager)
            await manager.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        foreach (var monitor in _monitors.Values)
            monitor.Dispose();

        _log.LogInformation("WatcherEngine stopped.");
    }
}
