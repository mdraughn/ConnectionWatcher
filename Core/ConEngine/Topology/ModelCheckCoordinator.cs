using System.Globalization;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>Ways ConnectionWatcher's map can disagree with what the probes prove (spec 2026-10-09 §4.6).</summary>
public enum ModelWarningKind
{
    /// <summary>A target is Down/Faltering while something beyond it answers: traffic passes it.</summary>
    HopBypassed,
    /// <summary>A target has never answered while something beyond it answers.</summary>
    NeverAnswered,
    /// <summary>Every anchor is the ISP's: "up" is proven only as far as the ISP.</summary>
    UnprovableUp,
    /// <summary>A hop keeps dropping its own probes while forwarding fine (spec §4.5, §4.6).</summary>
    ProbeOnlyLoss,
    /// <summary>Some, not all, system resolvers are Down while another answers.</summary>
    ResolverPartial,
    /// <summary>A resolver keeps returning wrong addresses for a seeded name.</summary>
    ResolverLying,
    /// <summary>A resolver blocks the seeded lookup names (a blocklist), so they can't prove resolution.</summary>
    ResolverFiltered,
    /// <summary>An upstream resolver is Down while a system resolver answers.</summary>
    UpstreamResolverDown,
    /// <summary>One lookup name misses on every answering resolver while other names hit.</summary>
    NameUnresolvable,
}

/// <summary>One condition that holds right now. Keyed so the same condition
/// on the same target is the same note across evaluations.</summary>
public sealed record ModelCondition(string Key, ModelWarningKind Kind, Guid? TargetId,
    string? TargetName, string Message)
{
    public static string KeyFor(ModelWarningKind kind, Guid? targetId)
    {
        var kindKey = kind switch
        {
            ModelWarningKind.HopBypassed => "hop-bypassed",
            ModelWarningKind.NeverAnswered => "never-answered",
            ModelWarningKind.UnprovableUp => "unprovable-up",
            ModelWarningKind.ProbeOnlyLoss => "probe-only-loss",
            ModelWarningKind.ResolverPartial => "resolver-partial",
            ModelWarningKind.ResolverLying => "resolver-lying",
            ModelWarningKind.ResolverFiltered => "resolver-filtered",
            ModelWarningKind.UpstreamResolverDown => "upstream-resolver-down",
            ModelWarningKind.NameUnresolvable => "name-unresolvable",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return targetId is { } id ? $"{kindKey}:{id:D}" : kindKey;
    }
}

/// <summary>A condition started (Active) or stopped holding.</summary>
public sealed record ModelConditionChange(ModelCondition Condition, bool Active, DateTimeOffset AtUtc);

/// <summary>
/// Evaluates the model-warning conditions from live monitor states (spec
/// 2026-10-09 §4.6). Admin-facing only: nothing here touches the verdict or
/// any target's state. Re-evaluates on every monitor transition and every
/// <see cref="EvaluateEvery"/> (the time-based conditions need a clock).
/// </summary>
public class ModelCheckCoordinator : IDisposable
{
    public static readonly TimeSpan EvaluateEvery = TimeSpan.FromSeconds(5);
    /// <summary>A Faltering hop is a note only once it has faltered this long;
    /// a Down hop is a note at once.</summary>
    public static readonly TimeSpan FalteringHold = TimeSpan.FromMinutes(2);
    /// <summary>How long a target may go without a single reply before it is a
    /// note at all — NeverAnswered. (Reply counters restart with every engine
    /// build, so a zero-reply hop is not yet evidence of a bypass.)</summary>
    public static readonly TimeSpan NeverAnsweredAfter = TimeSpan.FromMinutes(5);

    /// <summary>ProbeOnlyLoss needs this many usable windows (spec §9.3).</summary>
    public const int ProbeOnlyWindows = 10;
    public const double ProbeOnlyRaise = 0.15;
    public const double ProbeOnlyClear = 0.10;
    /// <summary>"Forwarding loss about 0" to raise, and the most it may grow to stay raised.</summary>
    public const double ForwardingRaise = 0.02;
    public const double ForwardingClear = 0.05;
    /// <summary>How long a ProbeOnlyLoss takes to prove itself from nothing.</summary>
    public static readonly TimeSpan ProbeOnlyProveTime = LossWindows.Span * ProbeOnlyWindows;

    public const string UnprovableUpMessage =
        "Every anchor is tagged as your ISP's, so ConnectionWatcher can only prove your ISP answers, " +
        "not the rest of the internet. Tag your ISP on a path hop (or add an anchor outside your ISP) " +
        "so ConnectionWatcher can prove your internet is up.";

    private const string BypassTail =
        " — traffic is passing it. Its probe may measure a service rather than forwarding, " +
        "it may not really be on the path, or your network has another route.";

    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    // Targets with at least one monitored descendant -> those descendants, nearest first.
    private readonly Dictionary<Guid, List<Guid>> _beyond = new();
    private readonly bool _unprovableUp;
    private readonly TimeProvider _time;
    private readonly ILogger<ModelCheckCoordinator> _log;
    private readonly LossWindows? _loss;
    private readonly ConEngine.Dns.DnsWatch _dns;
    private readonly ConEngine.Dns.DnsResultLog? _dnsResults;
    // Null: always. False during an outage, when a resolver Down for lack of a link
    // says nothing about the resolver (ResolverPartial and UpstreamResolverDown wait).
    private readonly Func<bool>? _internetReachable;
    private readonly Lock _lock = new();
    private readonly OrderedOutbox<ModelConditionChange> _outbox;
    private readonly Subject<ModelConditionChange> _changes = new();
    private readonly List<IDisposable> _subscriptions = new();

    // Guarded by _lock.
    private readonly Dictionary<string, ModelCondition> _active = new();
    private readonly Dictionary<Guid, DateTimeOffset> _falteringSince = new();
    private DateTimeOffset _startedUtc;
    private ITimer? _timer;
    private bool _disposed;

    public ModelCheckCoordinator(TargetGraph graph, IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        bool unprovableUp, TimeProvider timeProvider, ILogger<ModelCheckCoordinator> log,
        LossWindows? loss = null, ConEngine.Dns.DnsWatch? dnsWatch = null,
        ConEngine.Dns.DnsResultLog? dnsResults = null, Func<bool>? internetReachable = null)
    {
        _internetReachable = internetReachable;
        _monitors = monitors;
        _unprovableUp = unprovableUp;
        _time = timeProvider;
        _log = log;
        _outbox = new OrderedOutbox<ModelConditionChange>(change =>
        {
            _log.LogDebug("Model condition {Key} {State}", change.Condition.Key,
                change.Active ? "raised" : "cleared");
            _changes.OnNext(change);
        }, OutboxMode.Skip);
        _loss = loss;
        _dns = dnsWatch ?? ConEngine.Dns.DnsWatch.Off;
        _dnsResults = dnsResults;
        foreach (var id in monitors.Keys)
        {
            // DescendantIds is breadth-first: the first answering one is the nearest witness.
            var beyond = graph.DescendantIds(id, monitors.ContainsKey).ToList();
            if (beyond.Count > 0)
                _beyond[id] = beyond;
        }
    }

    /// <summary>Raise/clear changes. Published outside the coordinator's lock,
    /// in the order they were decided.</summary>
    public IObservable<ModelConditionChange> ChangeSource => _changes;

    /// <summary>The conditions holding as of the last evaluation.</summary>
    public IReadOnlyList<ModelCondition> Active
    {
        get { lock (_lock) { return _active.Values.ToList(); } }
    }

    /// <summary>Whether this condition holds as of the last evaluation. Takes
    /// only _lock; the coordinator publishes changes outside it and never calls
    /// the verdict, so a caller holding its own lock may call this.</summary>
    public bool Holds(ModelWarningKind kind, Guid targetId)
    {
        lock (_lock)
            return _active.ContainsKey(ModelCondition.KeyFor(kind, targetId));
    }

    public void Start()
    {
        lock (_lock)
            _startedUtc = _time.GetUtcNow();
        foreach (var monitor in _monitors.Values)
            _subscriptions.Add(monitor.TransitionSource.Subscribe(_ => OnTick()));
        _timer = _time.CreateTimer(_ => OnTick(), null, EvaluateEvery, EvaluateEvery);
        Evaluate();
    }

    private void OnTick()
    {
        try
        {
            Evaluate();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Model check evaluation failed");
        }
    }

    public void Evaluate()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var now = _time.GetUtcNow();
            var states = _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
            var found = new Dictionary<string, ModelCondition>();

            if (_unprovableUp)
                Add(found, ModelWarningKind.UnprovableUp, null, UnprovableUpMessage);

            foreach (var (id, beyond) in _beyond)
            {
                var state = states[id];
                if (state == HostState.Faltering)
                    _falteringSince.TryAdd(id, now);
                else
                    _falteringSince.Remove(id);
                // A hop that keeps dropping its own probes while forwarding fine
                // (spec §4.5 corollary 3, §9.3). A Down hop is HopBypassed's business.
                if (_loss is not null && !state.IsFailed())
                    CheckProbeOnlyLoss(found, _monitors[id], beyond);
                if (state is not (HostState.Down or HostState.Faltering))
                    continue;
                var witnessId = beyond.FirstOrDefault(d => states[d].IsAnswering());
                if (witnessId == Guid.Empty)
                    continue; // nothing beyond answers: a real failure, not a map error

                var hop = _monitors[id];
                var witness = _monitors[witnessId].Target.Name;
                var probe = ProbeText(hop.Target);
                bool neverAnswered = hop.AcceptedReplies + hop.RefusedReplies == 0;
                if (neverAnswered)
                {
                    // No condition until the wait is up: after a rebuild or restart the counters
                    // are zero, and a HopBypassed here would split an acknowledged NeverAnswered note.
                    if (now - _startedUtc >= NeverAnsweredAfter)
                        Add(found, ModelWarningKind.NeverAnswered, hop.Target,
                            $"{hop.Target.Name} has never answered its probe ({probe}) since monitoring started, " +
                            $"but {witness} beyond it answers. Change its probe or remove it from the path.");
                }
                else if (state == HostState.Down)
                {
                    Add(found, ModelWarningKind.HopBypassed, hop.Target,
                        $"{hop.Target.Name} isn't answering its probe ({probe}), but {witness} beyond it is{BypassTail}");
                }
                else if (now - _falteringSince[id] >= FalteringHold &&
                         !found.ContainsKey(ModelCondition.KeyFor(ModelWarningKind.ProbeOnlyLoss, id)))
                {
                    // A holding ProbeOnlyLoss already explains the faltering: one note, not two.
                    Add(found, ModelWarningKind.HopBypassed, hop.Target,
                        $"{hop.Target.Name} is losing replies to its probe ({probe}), but {witness} beyond it answers{BypassTail}");
                }
            }

            CheckResolvers(found, states);

            foreach (var key in _active.Keys.Where(k => !found.ContainsKey(k)).ToList())
            {
                _outbox.Enqueue(new ModelConditionChange(_active[key], false, now));
                _active.Remove(key);
            }
            foreach (var (key, condition) in found)
            {
                if (!_active.ContainsKey(key))
                    _outbox.Enqueue(new ModelConditionChange(condition, true, now));
                _active[key] = condition; // a standing condition refreshes its message silently
            }
        }
        _outbox.Drain();
    }

    private void CheckProbeOnlyLoss(Dictionary<string, ModelCondition> found, TargetMonitor hop, List<Guid> beyond)
    {
        var losses = _loss!.Closed
            .Select(w => LossAttribution.ForHop(w, hop.Target.Id, beyond))
            .OfType<LossAttribution.HopLoss>()
            .TakeLast(ProbeOnlyWindows)
            .ToList();
        if (losses.Count < ProbeOnlyWindows)
            return;
        double own = losses.Average(l => l.Own);
        double forwarding = losses.Average(l => l.Forwarding);
        double probeOnly = own - forwarding;
        bool active = _active.ContainsKey(ModelCondition.KeyFor(ModelWarningKind.ProbeOnlyLoss, hop.Target.Id));
        bool holds = active
            ? probeOnly >= ProbeOnlyClear && forwarding <= ForwardingClear
            : probeOnly >= ProbeOnlyRaise && forwarding <= ForwardingRaise;
        if (!holds)
            return;
        ProbeSpec.TryParse(hop.Target.Probe, out var probe);
        var advice = probe.Kind == ProbeKind.Tcp ? "consider a different probe" : "consider a TCP probe";
        Add(found, ModelWarningKind.ProbeOnlyLoss, hop.Target,
            $"{hop.Target.Name} drops some of its own pings but forwards fine (loss {Pct(own)} · " +
            $"forwarding {Pct(forwarding)} over the last {ProbeOnlyWindows} minutes) — " +
            $"common router behaviour; {advice}.");
    }

    private static string Pct(double value) => value.ToString("P0", CultureInfo.InvariantCulture);

    /// <summary>A DNS result's detail: "Filtered: NXDOMAIN (dns.google)" → "NXDOMAIN", the
    /// text between the first ": " and the last " (" — or, with no name suffix, the rest
    /// after the first ": " (or after the outcome token). Never throws on an odd message.</summary>
    public static string DetailOf(ConEngine.Dns.DnsResult result)
    {
        var message = result.Message.Trim();
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        var start = colon >= 0 ? colon + 2 : Math.Min(result.Outcome.Length, message.Length);
        var end = message.LastIndexOf(" (", StringComparison.Ordinal);
        if (end < start)
            end = message.Length;
        return message[start..end].Trim();
    }

    public const int ResolverWindow = 10;
    public const int ResolverRaise = 3;
    public const int NameMisses = 3;

    private void CheckResolvers(Dictionary<string, ModelCondition> found, IReadOnlyDictionary<Guid, HostState> states)
    {
        if (!_dns.Enabled || _dnsResults is null || _dns.System.Count == 0)
            return;
        var system = _dns.System.Where(m => states.ContainsKey(m.Target.Id))
            .Select(m => (Monitor: m, State: states[m.Target.Id])).ToList();
        var upstream = _dns.Upstream.Where(m => states.ContainsKey(m.Target.Id)).ToList();
        var answering = system.Where(s => s.State.IsAnswering()).ToList();
        var down = system.Where(s => s.State.IsFailed()).ToList();
        // The Down-based notes need a reachable internet: during an outage the resolvers
        // fall in whatever order their windows fill, which proves nothing about them.
        var reachable = _internetReachable?.Invoke() ?? true;

        // ResolverPartial: some, not all, system resolvers Down.
        if (reachable && answering.Count > 0 && down.Count > 0)
            foreach (var (monitor, _) in down)
                Add(found, ModelWarningKind.ResolverPartial, monitor.Target,
                    $"Your resolver {monitor.Target.Address} isn't answering; {answering[0].Monitor.Target.Address} is.");

        // UpstreamResolverDown: a system resolver answers but an upstream one is Down.
        if (reachable && answering.Count > 0)
            foreach (var up in upstream.Where(m => states[m.Target.Id].IsFailed()))
                Add(found, ModelWarningKind.UpstreamResolverDown, up.Target,
                    $"{up.Target.Address} isn't answering direct queries; your resolver is fine.");

        // ResolverLying / ResolverFiltered, per resolver, from the result log.
        foreach (var monitor in system.Select(s => s.Monitor).Concat(upstream))
        {
            var recent = _dnsResults.Recent(monitor.Target.Id);
            var judged = recent.Where(r => r.Outcome is "Filtered" or "WrongAnswer" or "Verified").TakeLast(ResolverWindow).ToList();
            var lies = judged.Where(r => r.Outcome == "WrongAnswer").ToList();
            if (lies.Count >= ResolverRaise)
            {
                var last = lies[^1];
                Add(found, ModelWarningKind.ResolverLying, monitor.Target,
                    $"{monitor.Target.Name} answered {last.Name ?? "a lookup name"} with {DetailOf(last)} — a captive portal or interception?");
            }
            else if (ConEngine.Dns.DnsSummary.IsFiltered(recent))
            {
                var last = judged.Last(r => r.Outcome == "Filtered");
                Add(found, ModelWarningKind.ResolverFiltered, monitor.Target,
                    $"{monitor.Target.Name} blocks {last.Name ?? "a lookup name"} (answered {DetailOf(last)}) — a Pi-hole or AdGuard blocklist? " +
                    "Name resolution still works; the seeded lookup names can't prove it. Add a lookup name your resolver allows.");
            }
        }

        // NameUnresolvable: one name missed its last NameMisses asks on every answering
        // resolver that asked it, while some other name hit on each of them.
        var answeringResolvers = system.Select(s => s.Monitor).Concat(upstream)
            .Where(m => states[m.Target.Id].IsAnswering()).ToList();
        if (answeringResolvers.Count == 0)
            return;
        var byName = new Dictionary<string, (int Resolvers, int Failing)>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in answeringResolvers)
        {
            var recent = _dnsResults.Recent(monitor.Target.Id);
            if (!recent.Any(r => r.Success && r.Name is not null))
                continue; // nothing hit here: no basis to single out a name
            foreach (var group in recent.Where(r => r.Name is not null).GroupBy(r => r.Name!, StringComparer.OrdinalIgnoreCase))
            {
                var lastAsks = group.TakeLast(NameMisses).ToList();
                var failing = lastAsks.Count == NameMisses && lastAsks.All(r => !r.Success);
                var entry = byName.GetValueOrDefault(group.Key);
                byName[group.Key] = (entry.Resolvers + 1, entry.Failing + (failing ? 1 : 0));
            }
        }
        foreach (var (name, (resolvers, failing)) in byName)
            if (resolvers == answeringResolvers.Count && failing == resolvers)
            {
                Add(found, ModelWarningKind.NameUnresolvable, null, $"{name} isn't resolving anywhere.");
                break; // one note; the key has no target
            }
    }

    private static void Add(Dictionary<string, ModelCondition> found, ModelWarningKind kind,
        Target? target, string message)
    {
        var key = ModelCondition.KeyFor(kind, target?.Id);
        found[key] = new ModelCondition(key, kind, target?.Id, target?.Name, message);
    }

    /// <summary>"tcp/80" for a TCP probe, "dns" for a DNS query, "ping" for ICMP echo.</summary>
    private static string ProbeText(Target target)
    {
        ProbeSpec.TryParse(target.Probe, out var probe);
        return probe.Kind switch
        {
            ProbeKind.Tcp => $"tcp/{probe.Port}",
            ProbeKind.Dns => "dns",
            _ => "ping",
        };
    }

    public void Dispose()
    {
        List<IDisposable> toDispose;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            toDispose = new List<IDisposable>(_subscriptions);
            _subscriptions.Clear();
        }
        foreach (var s in toDispose)
            s.Dispose();
        _changes.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
