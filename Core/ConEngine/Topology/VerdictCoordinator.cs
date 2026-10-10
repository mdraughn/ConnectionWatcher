using System.Collections.Concurrent;
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
    LinkDown
}

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
    /// never resolves — must never hide an outage for longer than this.</summary>
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
    private readonly TimeProvider _time;
    private readonly ILogger<VerdictCoordinator> _log;
    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<TargetTransition> _outbox = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Subject<TargetTransition> _transitionSubject = new();

    /// <summary>State, detail and blamed operator as one immutable value:
    /// readers on other threads get a consistent triple, never a torn one.</summary>
    public sealed record VerdictSnapshot(VerdictState State, string? Detail, string? Operator);

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

    public VerdictCoordinator(TargetGraph graph, PoolDefinition anchorPool,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors, int weatherThreshold,
        TimeProvider timeProvider, ILogger<VerdictCoordinator> log)
    {
        _graph = graph;
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
            var (verdict, detail, operatorName) = Classify(states);
            if (!_waitingThisPass)
                EndWait();
            var previous = Current;
            if (verdict == previous.State)
            {
                Current = previous with { Detail = detail, Operator = operatorName }; // detail carries live counts — always refresh
                return;
            }
            if (ignoreUnknown && verdict == VerdictState.Unknown)
                return; // a seeded verdict outranks "no anchors determined yet"

            Current = new VerdictSnapshot(verdict, detail, operatorName);
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

    /// <summary>Under _lock, from Classify: no counted anchor answers, but
    /// undetermined ones could still. True while the wait
    /// is within its cap. The first call of a wait starts the clock and arms a
    /// one-shot timer that reclassifies at the cap (Classify otherwise runs
    /// only on monitor transitions, and a stuck anchor emits none).</summary>
    private bool StillWaiting()
    {
        _waitingThisPass = true;
        if (_waitExpired)
            return false;
        var now = _time.GetUtcNow();
        if (_waitingSince is not { } since)
        {
            _waitingSince = now;
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

    private (VerdictState, string, string? Operator) Classify(IReadOnlyDictionary<Guid, HostState> states)
    {
        var anchors = _anchors.Select(a => (Monitor: a, State: states[a.Target.Id])).ToList();
        var determined = anchors.Where(a => !Undetermined(a.State)).ToList();
        if (determined.Count == 0)
            return (VerdictState.Unknown, "No anchors determined yet", null);

        // 1. Reachability: did packets get out and back? (spec §4.1, D2)
        // One answering counted anchor is enough (Mark's rule 2026-10-09).
        var counted = anchors.Where(a => _counted.Contains(a.Monitor)).ToList();
        if (counted.Any(a => Answering(a.State)))
            return Quality(determined, states);

        // None answering. Wait (uncapped) while NO anchor is determined (above).
        // Once one is — an ISP-owned anchor determined Down is evidence too —
        // LinkDown is not concluded while an undetermined counted anchor could
        // still answer (no blip on a cold start or after a recovery releases
        // the anchors while one is permanently dead) — but only for
        // AnchorWaitCap: an anchor stuck in Starting (an address that never
        // resolves) must not hide the outage longer (spec §4.2, I2).
        if (counted.Any(a => Undetermined(a.State)) && StillWaiting())
            return (VerdictState.Unknown, "Waiting for anchors to report", null);

        // 2. Down: the path says where (spec §4.3).
        return Blame(VerdictState.LinkDown, states);
    }

    /// <summary>The internet is reachable: how good is it? Anchor evidence only,
    /// except that weather our own path shares is our link (interim attribution;
    /// Plan D replaces it with burst-aligned loss attribution).</summary>
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

        // With fewer independent anchors than the weather threshold, weather can
        // never be diagnosed — say so rather than implying full confidence.
        var qualifier = determinedUnits < _weatherThreshold
            ? $" (only {determinedUnits} {(grouped ? "operator(s)" : "anchor(s)")} — cannot distinguish a site incident from internet weather)"
            : "";

        if (degraded.Count == 0)
            return (VerdictState.Healthy, $"Healthy: {determined.Count} anchors responding{qualifier}", null);
        if (degradedUnits < _weatherThreshold)
        {
            var sharedOperator = degraded.Count > 1 && degradedUnits == 1
                ? $" — all {degraded[0].Monitor.Target.Operator}" : "";
            return (VerdictState.SiteIncident, $"SiteIncident: {List()}{sharedOperator}{qualifier}", null);
        }

        // A Down hop that something beyond answers through is bypassed (its
        // probe lies): it carries no forwarding loss and is never blamed (M1).
        var losingHop = _ancestors
            .Where(a => states[a.Monitor.Target.Id] switch
            {
                HostState.Faltering => true,
                HostState.Down => !_descendants[a.Monitor.Target.Id].Any(id => Answering(states[id])),
                _ => false,
            })
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (losingHop.Monitor is not null)
            return Wording(VerdictState.LinkDegraded, losingHop.Monitor,
                $"{losingHop.Monitor.Target.Name} is {states[losingHop.Monitor.Target.Id]}");

        var counts = grouped
            ? $"{degradedUnits} of {determinedUnits} operators ({degraded.Count} of {determined.Count} anchors)"
            : $"{degraded.Count} of {determined.Count} anchors";
        return (VerdictState.InternetWeather, $"InternetWeather: {counts} degraded ({List()})", null);
    }

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
        VerdictState.LinkDown => HostState.Down,
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
