using System.Net;
using System.Reactive.Subjects;
using ConCore;
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
    private readonly ConcurrentDictionary<IPAddress, TargetMonitor> _routingTable = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Subject<TargetTransition> _transitionSubject = new();
    private readonly Subject<(Guid TargetId, PingResult Result)> _pingSampleSubject = new();

    private TargetGraph? _graph;
    private PingManager? _manager;
    private TopologyCoordinator? _topology;
    private CancellationTokenSource? _cts;

    private readonly VerdictOptions _verdictOptions;

    public WatcherEngine(TimeProvider timeProvider, IPingTransceiverFactory pingFactory,
        IDomainNameResolverFactory resolverFactory, ILoggerFactory loggerFactory,
        IOptions<WatcherOptions> options, IOptions<VerdictOptions>? verdictOptions = null)
    {
        _timeProvider = timeProvider;
        _pingFactory = pingFactory;
        _resolverFactory = resolverFactory;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _verdictOptions = verdictOptions?.Value ?? new VerdictOptions();
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

        // Verdict layer, if an anchor pool is configured.
        if (_verdictOptions.AnchorPool is { Length: > 0 } anchorPoolName)
        {
            var anchorPool = _graph.Pools.Values.FirstOrDefault(p =>
                p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase));
            if (anchorPool is null)
            {
                _log.LogWarning("Verdict disabled: anchor pool '{Pool}' not found", anchorPoolName);
            }
            else
            {
                Verdict = new VerdictCoordinator(_graph, anchorPool, _monitors,
                    _verdictOptions.WeatherThreshold, _timeProvider,
                    _loggerFactory.CreateLogger<VerdictCoordinator>());
                if (seed?.Verdict is { } verdictSeed)
                    Verdict.Seed(verdictSeed);
                _subscriptions.Add(Verdict.TransitionSource.Subscribe(_transitionSubject.OnNext));
                Verdict.Start();
            }
        }

        // Suspicion layer: works without an anchor pool (per-target glitches
        // only); sweeps need the pool.
        var suspicionPool = _verdictOptions.AnchorPool is { Length: > 0 } poolName
            ? _graph.Pools.Values.FirstOrDefault(p =>
                p.Name.Equals(poolName, StringComparison.OrdinalIgnoreCase))
            : null;
        Suspicion = new SuspicionCoordinator(_graph, suspicionPool, _monitors,
            _verdictOptions.WeatherThreshold, _timeProvider,
            _loggerFactory.CreateLogger<SuspicionCoordinator>());
        _subscriptions.Add(Suspicion.GlitchSource.Subscribe(_glitchSubject.OnNext));

        // Ping plumbing: one manager, results routed by IP.
        _manager = new PingManager(_timeProvider, _pingFactory, ct, _loggerFactory);
        _subscriptions.Add(_manager.PingResultSource.Subscribe(
            result =>
            {
                if (_routingTable.TryGetValue(result.IpAddress, out var monitor))
                {
                    monitor.Discipline.OnPingResult(result.Success);
                    monitor.RecordPingResult(result);
                    _pingSampleSubject.OnNext((monitor.Target.Id, result));
                    Suspicion?.OnPingSample(monitor.Target.Id, result);
                }
            },
            ex => _log.LogError(ex, "Error in ping result stream"),
            () => _log.LogInformation("Ping result stream completed")));

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
        _routingTable[ip] = monitor;
        // Bursts shorten the timeout: replies arrive in well under a second,
        // and against a dead host the steady timeout dominates burst cost.
        var steadyTimeoutMs = (int)monitor.Profile.PingTimeout.TotalMilliseconds;
        ProbeSpec.TryParse(monitor.Target.Probe, out var probe);
        // Bursts shorten the timeout — but a refused TCP connect takes ~2s to
        // surface on Windows (the stack retries after the RST), so TCP probes
        // keep enough budget to classify Refused as a reply during bursts.
        var burstCapMs = probe.Kind == ProbeKind.Tcp ? 2500 : 1000;
        _manager!.GetOrCreateController(ip, probe, monitor.Discipline,
            () => monitor.Discipline.IsBurstActive ? Math.Min(steadyTimeoutMs, burstCapMs) : steadyTimeoutMs);
        // Warm starts burst everything so presumed states confirm in seconds
        // (seeded Faltering/Down already burst via the discipline; this covers
        // Responding/Starting, and no-ops on parked or already-bursting ones).
        if (_warmStart)
            monitor.Discipline.TriggerBurst();
        _log.LogInformation("Activated {Target} at {Ip}", monitor.Target.Name, ip);
    }

    private async Task ResolveAndActivateAsync(TargetMonitor monitor, string hostname, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(30);
        var maxBackoff = TimeSpan.FromMinutes(5);
        using var resolver = _resolverFactory.Create(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await resolver.ResolveAsync(hostname, 5000).ConfigureAwait(false);
                var ip = result.IPv4Addresses.FirstOrDefault();
                if (ip is not null)
                {
                    Activate(monitor, ip);
                    return;
                }
                monitor.ReportResolutionFailure($"No IPv4 address for '{hostname}'");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DNS resolution failed for {Hostname}", hostname);
                monitor.ReportResolutionFailure($"DNS lookup failed for '{hostname}': {ex.Message}");
            }

            try
            {
                await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
        }
    }

    public Task StopAsync()
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
        _manager?.Dispose();
        foreach (var monitor in _monitors.Values)
            monitor.Dispose();

        _log.LogInformation("WatcherEngine stopped.");
        return Task.CompletedTask;
    }
}
