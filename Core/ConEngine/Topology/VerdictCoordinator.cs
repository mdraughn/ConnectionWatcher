using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>Overall internet health classification.</summary>
public enum VerdictState
{
    Unknown,
    Healthy,
    SiteIncident,
    InternetWeather,
    LinkDegraded,
    DnsDown,
    LinkDown
}

public enum DnsBlameKind { YourResolver, NoResolver }

/// <summary>Why name lookups fail while the internet is reachable (spec 2026-10-10 §4.4).</summary>
public sealed record DnsBlame(DnsBlameKind Kind, string Message, IReadOnlyList<string> Resolvers);

/// <summary>
/// Classifies overall internet health (spec 2026-10-09 §4.1–4.3). The ANCHORS
/// decide whether the internet is reachable — one answering anchor that is
/// not the ISP's (the ISP: an operator tag on the path; none tagged, none
/// is the ISP's) — and how good it is (Healthy / SiteIncident /
/// InternetWeather). The PATH only assigns blame when the anchors say it is
/// down: the first silent hop after the last one with proven forwarding.
/// Evidence from farther away outranks evidence from nearer.
/// </summary>
public class VerdictCoordinator : IDisposable
{
    /// <summary>Fixed synthetic subject id for the "Internet" verdict stream.</summary>
    public static readonly Guid SubjectId = Guid.Parse("6E7E12E7-0000-4000-8000-000000000001");

    /// <summary>The verdict subject's name in transitions, Internet-class
    /// glitches and the metrics series.</summary>
    public const string SubjectName = "Internet";

    /// <summary>The longest the verdict waits for undetermined anchors that
    /// could still answer before concluding LinkDown from the ones that have
    /// reported (spec §4.2): a stuck anchor — an address that
    /// never resolves — must never hide an outage for longer than this. The same
    /// cap bounds the wait for undetermined system resolvers before DnsDown.</summary>
    public static readonly TimeSpan AnchorWaitCap = TimeSpan.FromSeconds(30);

    private readonly TargetGraph _graph;
    private readonly IReadOnlyList<TargetMonitor> _anchors;
    private readonly IReadOnlyList<(TargetMonitor Monitor, int Depth)> _ancestors;
    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _all;
    // Each anchor with its path, root first (monitored hops only).
    private readonly List<(TargetMonitor Anchor, List<TargetMonitor> Path)> _paths = new();
    private readonly Dictionary<Guid, List<Guid>> _descendants = new();
    // The anchors whose answer proves the internet is up: those outside the
    // ISP (all of them when the ISP is unknown, or when every one is the ISP's).
    private readonly List<TargetMonitor> _counted;
    private readonly int _weatherThreshold;
    private readonly ConEngine.Dns.DnsWatch _dns;
    private readonly ConEngine.Dns.DnsResultLog? _dnsResults;
    private readonly TimeProvider _time;
    private readonly ILogger<VerdictCoordinator> _log;
    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<TargetTransition> _outbox = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Subject<TargetTransition> _transitionSubject = new();

    /// <summary>State, detail and blamed operator as one immutable value:
    /// readers on other threads get a consistent triple, never a torn one.</summary>
    public sealed record VerdictSnapshot(VerdictState State, string? Detail, string? Operator, DnsBlame? Dns = null);

    public VerdictSnapshot Current { get; private set; } = new(VerdictState.Unknown, null, null);

    public VerdictState CurrentVerdict => Current.State;
    public string? CurrentDetail => Current.Detail;
    /// <summary>The ISP/operator being blamed by a Link* verdict (from the
    /// blamed trunk target's Operator tag), else null.</summary>
    public string? CurrentOperator => Current.Operator;
    /// <summary>Every anchor is the ISP's, so they all count as a weaker
    /// fallback (spec §4.1 decision (a)): the UnprovableUp model warning.</summary>
    public bool UsingIspFallback { get; }
    /// <summary>Transitions this coordinator raises as its own state changes. A
    /// queued transition may be delivered on another thread after the
    /// triggering call has already returned. If a subscriber throws, any
    /// transitions still queued behind it are delivered with the coordinator's
    /// next transition.</summary>
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

    // Guarded by _lock. When the current wait for undetermined anchors began
    // (null: not waiting), the one-shot timer that ends it at the cap, and
    // whether that timer has fired: the timer, not the clock, is authoritative
    // (a system timer may fire a tick early, the wall clock may step back).
    private DateTimeOffset? _waitingSince;
    private ITimer? _waitTimer;
    private bool _waitExpired;
    private bool _waitingThisPass;
    private bool _disposed;

    // Guarded by _lock. When the internet last became reachable (a counted
    // anchor answering after a pass in which none did, the first such pass
    // included), and whether the previous pass found it reachable. A system
    // resolver judged Down before that moment has no evidence of its own since
    // the internet came back (its window filled during the outage).
    private DateTimeOffset? _reachableSince;
    private bool _wasReachable;

    public VerdictCoordinator(TargetGraph graph, PoolDefinition anchorPool,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors, int weatherThreshold,
        TimeProvider timeProvider, ILogger<VerdictCoordinator> log,
        ConEngine.Dns.DnsWatch? dnsWatch = null, ConEngine.Dns.DnsResultLog? dnsResults = null)
    {
        _graph = graph;
        _dns = dnsWatch ?? ConEngine.Dns.DnsWatch.Off;
        _dnsResults = dnsResults;
        _weatherThreshold = weatherThreshold;
        _time = timeProvider;
        _log = log;
        _anchors = anchorPool.MemberTargetIds
            .Where(monitors.ContainsKey).Select(id => monitors[id]).ToList();

        // Union of ancestors of all anchors, with depth (root = 0). An anchor
        // nested under another anchor is not a path hop: it never names the
        // ISP and is never the losing hop (final review T4 M1).
        var anchorIds = _anchors.Select(a => a.Target.Id).ToHashSet();
        var seen = new Dictionary<Guid, int>();
        foreach (var anchor in _anchors)
        {
            var chain = new List<Guid>();
            var parent = _graph.Targets[anchor.Target.Id].ParentId;
            while (parent is { } pid)
            {
                chain.Add(pid);
                parent = _graph.Targets[pid].ParentId;
            }
            chain.Reverse(); // root first => index == depth
            for (int depth = 0; depth < chain.Count; depth++)
                if (!anchorIds.Contains(chain[depth]))
                    seen[chain[depth]] = depth;
        }
        _ancestors = seen.Where(kv => monitors.ContainsKey(kv.Key))
            .Select(kv => (monitors[kv.Key], kv.Value)).ToList();

        _all = monitors;
        foreach (var anchor in _anchors)
        {
            var path = new List<TargetMonitor>();
            var parent = _graph.Targets[anchor.Target.Id].ParentId;
            while (parent is { } pid)
            {
                if (monitors.TryGetValue(pid, out var hop))
                    path.Add(hop);
                parent = _graph.Targets[pid].ParentId;
            }
            path.Reverse();
            _paths.Add((anchor, path));
        }
        // Every hop on any anchor's path (a nested anchor included: it still
        // proves forwarding for what lies beyond it).
        foreach (var hop in _paths.SelectMany(p => p.Path))
        {
            _descendants.TryAdd(hop.Target.Id, _graph.GetDescendants(hop.Target.Id)
                .Select(d => d.Id).Where(monitors.ContainsKey).ToList());
        }
        // The ISP is whoever operates the path; an anchor sharing that tag is
        // the ISP's and never proves the internet is up. Untagged anchors are
        // not the ISP's — with no ISP tag anywhere, every anchor counts.
        var isp = _ancestors.Select(a => Normalize(a.Monitor.Target.Operator))
            .OfType<string>().ToHashSet();
        _counted = _anchors.Where(a => Normalize(a.Target.Operator) is not { } op || !isp.Contains(op)).ToList();
        // Decision (a): if every anchor is the ISP's, they are all there is —
        // count them all. A weaker check (the ISP's own servers can answer
        // while the ISP is cut off from the rest of the internet).
        if (_counted.Count == 0)
        {
            UsingIspFallback = _anchors.Count > 0;
            _counted = _anchors.ToList();
        }
    }

    // Guarded by _lock. The model check whose ProbeOnlyLoss notes veto blaming a
    // hop (null: no veto). The veto is the note itself, so the banner and the
    // note can't disagree. Windows aren't carried across a restart or rebuild,
    // so there is no veto for about 10 minutes after one.
    private ModelCheckCoordinator? _probeOnlyVeto;

    /// <summary>Never blame a Faltering path hop while its ProbeOnlyLoss note
    /// holds: the note already proves it forwards fine. Reclassifies whenever
    /// such a note is raised or cleared. Call once, before the check starts.
    /// Lock order is this _lock, then the check's (Holds); the check publishes
    /// outside its lock and never calls the verdict.</summary>
    public void UseProbeOnlyVeto(ModelCheckCoordinator check)
    {
        lock (_lock)
        {
            if (_disposed || _probeOnlyVeto is not null)
                return;
            _probeOnlyVeto = check;
            _subscriptions.Add(check.ChangeSource
                .Where(c => c.Condition.Kind == ModelWarningKind.ProbeOnlyLoss)
                .Subscribe(_ => Recalculate()));
        }
    }

    private bool _seeded;

    /// <summary>Warm restart: adopt the last known verdict silently — no
    /// transition. Start() then reclassifies once from the (seeded) monitor
    /// states, emitting only if they contradict this presumption.</summary>
    public void Seed(VerdictState verdict)
    {
        lock (_lock)
        {
            if (CurrentVerdict != VerdictState.Unknown)
                return;
            Current = new VerdictSnapshot(verdict, null, null);
            _seeded = true;
            // A presumed-reachable verdict means the internet was already up: the first
            // pass is not a return, so no resolver is judged stale for lack of results
            // (the result log is fresh per engine).
            _wasReachable = verdict is not (VerdictState.LinkDown or VerdictState.Unknown);
        }
    }

    /// <summary>Subscribes to every monitor (proof of forwarding can come from
    /// any descendant). Call once after monitors exist.</summary>
    public void Start()
    {
        foreach (var m in _all.Values)
            _subscriptions.Add(m.TransitionSource.Subscribe(_ => Recalculate()));

        // A fully confirmed warm start emits no monitor transitions at all, so
        // a seeded verdict must classify once now rather than wait for one.
        bool seeded;
        lock (_lock) { seeded = _seeded; _seeded = false; }
        if (seeded)
            Recalculate(ignoreUnknown: true);
    }

    private void Recalculate(bool ignoreUnknown = false)
    {
        lock (_lock)
        {
            if (_disposed)
                return; // a wait-cap timer callback racing Dispose
            // One read per monitor: classification, detail and blame all come
            // from the same snapshot, so the detail can never contradict the state.
            var states = _all.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
            _waitingThisPass = false;
            var (verdict, detail, operatorName, dns) = Classify(states);
            if (!_waitingThisPass)
                EndWait();
            var previous = Current;
            if (verdict == previous.State)
            {
                Current = previous with { Detail = detail, Operator = operatorName, Dns = dns }; // detail carries live counts — always refresh
                return;
            }
            if (ignoreUnknown && verdict == VerdictState.Unknown)
                return; // a seeded verdict outranks "no anchors determined yet"

            Current = new VerdictSnapshot(verdict, detail, operatorName, dns);
            var transition = new TargetTransition
            {
                Kind = TransitionSubjectKind.Verdict,
                TargetId = SubjectId,
                TargetName = SubjectName,
                From = Map(previous.State),
                To = Map(verdict),
                Timestamp = _time.GetUtcNow(),
                Reason = new TransitionReason
                {
                    Trigger = TransitionTrigger.VerdictChanged,
                    Verdict = verdict.ToString(),
                    Detail = detail,
                    Dns = dns,
                },
            };
            // Queued under the lock (queue order is state order), published after it.
            _outbox.Enqueue(transition);
        }
        DrainOutbox();
    }

    /// <summary>
    /// Publishes queued transitions in state order, never under _lock.
    /// TryEnter, not Enter: a thread that finds another one draining leaves
    /// its item to that thread, which re-checks the queue after releasing the
    /// lock — nothing is stranded, and no thread waits on this coordinator
    /// while it may itself be inside a monitor's publish.
    /// </summary>
    private void DrainOutbox()
    {
        while (!_outbox.IsEmpty)
        {
            if (!_publishLock.TryEnter())
                return;
            try
            {
                while (_outbox.TryDequeue(out var transition))
                {
                    _log.LogInformation("Verdict: {Transition}", transition);
                    _transitionSubject.OnNext(transition);
                }
            }
            finally
            {
                _publishLock.Exit();
            }
        }
    }

    private enum WaitKind { Anchors, Resolvers }
    private WaitKind _waitKind;

    /// <summary>Under _lock, from Classify. Two waits share one clock and timer, told apart
    /// by <paramref name="kind"/>: Anchors (no counted anchor answers, but undetermined
    /// ones could still) and Resolvers (every determined system resolver is Down, but an
    /// undetermined one could still answer). Starting a wait of one kind while the other
    /// is in progress ends the other first, so neither inherits the other's clock or
    /// expiry. True while the wait is within its cap. The first call of a wait starts the clock and arms a
    /// one-shot timer that reclassifies at the cap (Classify otherwise runs
    /// only on monitor transitions, and a stuck anchor emits none).</summary>
    private bool StillWaiting(WaitKind kind)
    {
        _waitingThisPass = true;
        if (_waitingSince is not null && _waitKind != kind)
            EndWait(); // a different wait never inherits this one's clock or expiry
        if (_waitExpired)
            return false;
        var now = _time.GetUtcNow();
        if (_waitingSince is not { } since)
        {
            _waitingSince = now;
            _waitKind = kind;
            _waitTimer?.Dispose();
            // The callback takes _lock before reading `timer`, and this
            // assignment happens under _lock: it always sees its own instance.
            ITimer? timer = null;
            timer = _time.CreateTimer(_ => OnWaitCap(timer), null, AnchorWaitCap, Timeout.InfiniteTimeSpan);
            _waitTimer = timer;
            return true;
        }
        return now - since < AnchorWaitCap;
    }

    /// <summary>Timer thread: the cap has passed. Authoritative — the clock
    /// may read a tick short of the cap here — and never throws.</summary>
    private void OnWaitCap(ITimer? timer)
    {
        try
        {
            lock (_lock)
            {
                if (timer is null || !ReferenceEquals(timer, _waitTimer))
                    return; // stale: that wait already ended or was replaced
                _waitExpired = true;
            }
            Recalculate();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Verdict wait-cap reclassification failed");
        }
    }

    /// <summary>Under _lock: the waiting condition has cleared.</summary>
    private void EndWait()
    {
        _waitingSince = null;
        _waitExpired = false;
        _waitTimer?.Dispose();
        _waitTimer = null;
    }

    private static bool Answering(HostState s) => TopologyCoordinator.Answering(s);

    private static bool Undetermined(HostState s) => s is HostState.Initializing or HostState.Starting;

    private static string? Normalize(string? op) =>
        op?.Trim() is { Length: > 0 } t ? t.ToLowerInvariant() : null;

    private (VerdictState, string, string? Operator, DnsBlame? Dns) Classify(IReadOnlyDictionary<Guid, HostState> states)
    {
        var anchors = _anchors.Select(a => (Monitor: a, State: states[a.Target.Id])).ToList();
        var counted = anchors.Where(a => _counted.Contains(a.Monitor)).ToList();
        var reachable = counted.Any(a => Answering(a.State));
        if (reachable && !_wasReachable)
            _reachableSince = _time.GetUtcNow();
        _wasReachable = reachable;

        var determined = anchors.Where(a => !Undetermined(a.State)).ToList();
        if (determined.Count == 0)
            return (VerdictState.Unknown, "No anchors determined yet", null, null);

        // 1. Reachability: did packets get out and back? (spec §4.1, D2)
        // One answering counted anchor is enough (Mark's rule 2026-10-09).
        if (reachable)
        {
            // Reachable. Name resolution next (spec 2026-10-10 §4.4): it outranks quality.
            var dns = CheckDns(states);
            if (dns is { } down)
                return down;
            var (state, detail, op) = Quality(determined, states);
            return (state, detail, op, null);
        }

        // None answering. Wait (uncapped) while NO anchor is determined (above).
        // Once one is — an ISP-owned anchor determined Down is evidence too —
        // LinkDown is not concluded while an undetermined counted anchor could
        // still answer (no blip on a cold start or after a recovery releases
        // the anchors while one is permanently dead) — but only for
        // AnchorWaitCap: an anchor stuck in Starting (an address that never
        // resolves) must not hide the outage longer (spec §4.2, I2).
        if (counted.Any(a => Undetermined(a.State)) && StillWaiting(WaitKind.Anchors))
            return (VerdictState.Unknown, "Waiting for anchors to report", null, null);

        // 2. Down: the path says where (spec §4.3).
        var (linkDown, downDetail, downOp) = Blame(VerdictState.LinkDown, states);
        return (linkDown, AllSystemResolversDown(states) ? downDetail + " — name lookups are failing too" : downDetail, downOp, null);
    }

    private bool AllSystemResolversDown(IReadOnlyDictionary<Guid, HostState> states) =>
        _dns.Enabled && _dns.System.Count > 0 &&
        _dns.System.All(m => states[m.Target.Id] is HostState.Down or HostState.Unreachable);

    /// <summary>DnsDown when every determined system resolver is Down (and none is still
    /// undetermined past the wait cap); null otherwise, including while an undetermined
    /// resolver is still within the cap (the quality verdict stands meanwhile, the timer
    /// stays armed and the cap concludes DnsDown). Upstream resolvers decide the blame.
    /// A Down resolver without fresh evidence (<see cref="WithoutFreshFailure"/>) counts
    /// as undetermined: after an outage its window is full of the outage's failures.</summary>
    private (VerdictState, string, string?, DnsBlame?)? CheckDns(IReadOnlyDictionary<Guid, HostState> states)
    {
        if (!_dns.Enabled || _dns.System.Count == 0)
            return null;
        var system = _dns.System.Select(m => (Monitor: m, State: states[m.Target.Id])).ToList();
        var down = system.Where(s => s.State is HostState.Down or HostState.Unreachable).ToList();
        var stale = down.Where(s => WithoutFreshFailure(s.Monitor)).ToList();
        var determined = system.Where(s => !Undetermined(s.State) && !stale.Contains(s)).ToList();
        if (determined.Count == 0 && stale.Count == 0)
            return null;
        if (!determined.All(s => s.State is HostState.Down or HostState.Unreachable))
            return null;
        if ((stale.Count > 0 || system.Any(s => Undetermined(s.State))) && StillWaiting(WaitKind.Resolvers))
            return null; // keep waiting (timer armed) but never hide a reachable internet's quality verdict

        var resolvers = system.Select(s => s.Monitor.Target.Address).ToList();
        var firstDown = down[0].Monitor.Target.Address;
        var upstream = _dns.Upstream.FirstOrDefault(m => Answering(states[m.Target.Id]));
        DnsBlame blame = upstream is not null
            ? new DnsBlame(DnsBlameKind.YourResolver,
                $"Your resolver {firstDown} isn't answering, but {upstream.Target.Address} answers directly.", resolvers)
            : new DnsBlame(DnsBlameKind.NoResolver,
                "No resolver is answering, though the internet is reachable." +
                (_dns.Upstream.Count == 0 ? " Name a public resolver in Config → DNS so ConnectionWatcher can tell your resolver from everyone's." : ""),
                resolvers);
        return (VerdictState.DnsDown, $"DnsDown: {blame.Message}", null, blame);
    }

    /// <summary>Under _lock: a Down system resolver whose latest DNS result is not a
    /// failure later than <see cref="_reachableSince"/> — none at all, one from before
    /// the internet came back, or a fresh answer that has not yet lifted it out of
    /// Down (its burst is re-proving it) — that last only within the climb window,
    /// <see cref="AnchorWaitCap"/> after the return, so a mostly-dead resolver that
    /// answers now and then cannot flap a steady DnsDown. False when no result log was
    /// given or the internet has not returned (a seeded reachable verdict).</summary>
    private bool WithoutFreshFailure(TargetMonitor resolver)
    {
        if (_dnsResults is null || _reachableSince is not { } since)
            return false;
        var recent = _dnsResults.Recent(resolver.Target.Id);
        if (recent.Count == 0 || recent[^1].At <= since)
            return true;
        return recent[^1].Success && _time.GetUtcNow() - since <= AnchorWaitCap;
    }

    /// <summary>The internet is reachable: how good is it? Anchor evidence decides
    /// quality. Weather is our link's when a path hop is Faltering (or Down with
    /// nothing answering beyond it); the SHALLOWEST such hop is blamed — where
    /// the loss starts. A Faltering hop whose ProbeOnlyLoss note holds is never
    /// blamed (the cheap veto); the statistical Plan D2 is parked (spec 2026-10-13-paired-probe-veto-design.md).</summary>
    private (VerdictState, string, string? Operator) Quality(
        List<(TargetMonitor Monitor, HostState State)> determined, IReadOnlyDictionary<Guid, HostState> states)
    {
        var degraded = determined.Where(a => a.State
            is HostState.Down or HostState.Faltering or HostState.Recovering).ToList();
        string List() => string.Join(", ", degraded.Select(a => $"{a.Monitor.Target.Name}={a.State}"));

        // Weather needs INDEPENDENT evidence. Anchors that share an operator
        // ("Microsoft" four times, one per A record) share a path and fail
        // together, so they count once; untagged anchors count individually.
        var determinedUnits = CountOperators(determined.Select(a => a.Monitor));
        var degradedUnits = CountOperators(degraded.Select(a => a.Monitor));
        var grouped = degradedUnits != degraded.Count || determinedUnits != determined.Count;

        // An Unreachable anchor is dead because a hop above it died: it is not
        // evidence about the internet, so it is neither counted as responding nor
        // as an independent anchor (ruling D5).
        var unreachable = determined.Where(a => a.State == HostState.Unreachable).ToList();
        var counted = determined.Where(a => a.State != HostState.Unreachable).ToList();
        var countedUnits = CountOperators(counted.Select(a => a.Monitor));
        var countedGrouped = degradedUnits != degraded.Count || countedUnits != counted.Count;

        // With fewer independent anchors than the weather threshold, weather can
        // never be diagnosed — say so rather than implying full confidence.
        var qualifier = countedUnits < _weatherThreshold
            ? $" (only {countedUnits} {(countedGrouped ? "operator(s)" : "anchor(s)")} — cannot distinguish a site incident from internet weather)"
            : "";

        if (degraded.Count == 0)
        {
            var responding = counted.Count(a => Answering(a.State));
            var unreachableNote = unreachable.Count == 0 ? "" :
                $" ({string.Join(", ", unreachable.Select(a => UnreachableName(a.Monitor)))})";
            return (VerdictState.Healthy, $"Healthy: {responding} anchors responding{unreachableNote}{qualifier}", null);
        }
        if (degradedUnits < _weatherThreshold)
        {
            var sharedOperator = degraded.Count > 1 && degradedUnits == 1
                ? $" — all {degraded[0].Monitor.Target.Operator}" : "";
            return (VerdictState.SiteIncident, $"SiteIncident: {List()}{sharedOperator}{qualifier}", null);
        }

        // Is the weather our own path's? The state rule: the SHALLOWEST path hop
        // that is Faltering, or Down with nothing answering beyond it (loss that
        // starts at the router shows at the modem too; it starts where it first
        // shows). A Down hop that something beyond answers through is bypassed
        // (its probe lies): never blamed (M1). A Faltering hop whose ProbeOnlyLoss
        // note holds drops only its own pings: never blamed (the note needs ~10 min
        // to prove itself, so until then it is blamed).
        var losingHop = _ancestors
            .Where(a => states[a.Monitor.Target.Id] switch
            {
                HostState.Faltering => _probeOnlyVeto?.Holds(ModelWarningKind.ProbeOnlyLoss, a.Monitor.Target.Id) != true,
                HostState.Down => !_descendants[a.Monitor.Target.Id].Any(id => Answering(states[id])),
                _ => false,
            })
            .OrderBy(a => a.Depth).FirstOrDefault();
        if (losingHop.Monitor is not null)
            return Wording(VerdictState.LinkDegraded, losingHop.Monitor,
                $"{losingHop.Monitor.Target.Name} is {states[losingHop.Monitor.Target.Id]}");

        var counts = grouped
            ? $"{degradedUnits} of {determinedUnits} operators ({degraded.Count} of {determined.Count} anchors)"
            : $"{degraded.Count} of {determined.Count} anchors";
        return (VerdictState.InternetWeather, $"InternetWeather: {counts} degraded ({List()})", null);
    }

    /// <summary>"Quad9 unreachable behind ModemB": the anchor's name and the target its
    /// Unreachable state is explained by; just "Quad9 unreachable" with no known cause.</summary>
    private string UnreachableName(TargetMonitor anchor) =>
        anchor.ExplainedBy is { } cause && _graph.Targets.TryGetValue(cause, out var target)
            ? $"{anchor.Target.Name} unreachable behind {target.Name}"
            : $"{anchor.Target.Name} unreachable";

    /// <summary>Frontier blame: along each failing anchor's path, the last hop
    /// with proven forwarding (it or anything beyond it answers) is the
    /// frontier; the hop after it is blamed. The shallowest blame wins among
    /// anchors that HAVE a path; an anchor hung off the root says nothing
    /// about where, so "no anchor is answering" is used only when no failing
    /// anchor has a path (final review T4 M2).</summary>
    private (VerdictState, string, string? Operator) Blame(
        VerdictState verdict, IReadOnlyDictionary<Guid, HostState> states)
    {
        bool Proven(TargetMonitor hop) =>
            Answering(states[hop.Target.Id]) ||
            _descendants[hop.Target.Id].Any(id => Answering(states[id]));

        TargetMonitor? blamed = null, beyond = null;
        int bestIndex = int.MaxValue;
        foreach (var (anchor, path) in _paths)
        {
            if (Answering(states[anchor.Target.Id]) || path.Count == 0)
                continue;
            int frontier = -1;
            for (int i = 0; i < path.Count; i++)
                if (Proven(path[i]))
                    frontier = i;
            int index = frontier + 1;
            if (index >= bestIndex)
                continue;
            bestIndex = index;
            (blamed, beyond) = index < path.Count
                ? (path[index], (TargetMonitor?)null)
                : ((TargetMonitor?)null, path.Count > 0 ? path[^1] : null);
        }

        if (blamed is not null)
            return Wording(verdict, blamed, $"{blamed.Target.Name} is {states[blamed.Target.Id]}");
        if (beyond is not null)
            return Wording(verdict, beyond, $"nothing answers beyond {beyond.Target.Name}");
        // No failing anchor has a path (anchors hung off the root).
        return (verdict, $"{verdict}: no anchor is answering", null);
    }

    /// <summary>Link verdicts name the blamed hop's operator ("Comcast") when tagged.</summary>
    private static (VerdictState, string, string?) Wording(VerdictState verdict, TargetMonitor hop, string what)
    {
        var op = hop.Target.Operator;
        var text = op is { Length: > 0 } ? $"{verdict}: {op} — {what}" : $"{verdict}: {what}";
        return (verdict, text, op is { Length: > 0 } ? op : null);
    }

    /// <summary>Distinct operators among the anchors; an untagged anchor is its own.</summary>
    private static int CountOperators(IEnumerable<TargetMonitor> anchors) =>
        anchors.Select(a => a.Target.Operator?.Trim() is { Length: > 0 } op
                ? "op:" + op.ToLowerInvariant()
                : "id:" + a.Target.Id)
            .Distinct().Count();

    private static HostState Map(VerdictState v) => v switch
    {
        VerdictState.Healthy or VerdictState.SiteIncident => HostState.Responding,
        VerdictState.InternetWeather or VerdictState.LinkDegraded => HostState.Faltering,
        VerdictState.DnsDown or VerdictState.LinkDown => HostState.Down,
        _ => HostState.Starting,
    };

    public void Dispose()
    {
        List<IDisposable> toDispose;
        lock (_lock)
        {
            _disposed = true;
            EndWait();
            toDispose = new List<IDisposable>(_subscriptions);
            _subscriptions.Clear();
        }
        if (toDispose.Count == 0)
            return; // already disposed
        foreach (var s in toDispose)
            s.Dispose();
        _transitionSubject.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
