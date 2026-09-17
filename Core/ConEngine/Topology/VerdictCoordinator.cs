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
/// Classifies overall internet health from anchor-pool member states and their
/// ancestors: Healthy / SiteIncident / InternetWeather / LinkDegraded / LinkDown.
/// A Down ancestor always outranks anchor evidence (you can't blame the weather
/// when your own link is down).
/// </summary>
public class VerdictCoordinator : IDisposable
{
    /// <summary>Fixed synthetic subject id for the "Internet" verdict stream.</summary>
    public static readonly Guid SubjectId = Guid.Parse("6E7E12E7-0000-4000-8000-000000000001");

    private readonly TargetGraph _graph;
    private readonly IReadOnlyList<TargetMonitor> _anchors;
    private readonly IReadOnlyList<(TargetMonitor Monitor, int Depth)> _ancestors;
    private readonly int _weatherThreshold;
    private readonly TimeProvider _time;
    private readonly ILogger<VerdictCoordinator> _log;
    private readonly Lock _lock = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Subject<TargetTransition> _transitionSubject = new();

    public VerdictState CurrentVerdict { get; private set; } = VerdictState.Unknown;
    public string? CurrentDetail { get; private set; }

    /// <summary>The ISP/operator being blamed by a Link* verdict (from the
    /// blamed trunk target's Operator tag), else null.</summary>
    public string? CurrentOperator { get; private set; }
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

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

        // Union of ancestors of all anchors, with depth (root = 0).
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
                seen[chain[depth]] = depth;
        }
        _ancestors = seen.Where(kv => monitors.ContainsKey(kv.Key))
            .Select(kv => (monitors[kv.Key], kv.Value)).ToList();
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
            CurrentVerdict = verdict;
            _seeded = true;
        }
    }

    /// <summary>Subscribes to anchors and their ancestors. Call once after monitors exist.</summary>
    public void Start()
    {
        foreach (var m in _anchors.Concat(_ancestors.Select(a => a.Monitor)).Distinct())
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
            var (verdict, detail, operatorName) = Classify();
            CurrentDetail = detail; // detail carries live counts — always refresh
            CurrentOperator = operatorName;
            if (verdict == CurrentVerdict)
                return;
            if (ignoreUnknown && verdict == VerdictState.Unknown)
                return; // a seeded verdict outranks "no anchors determined yet"

            var from = Map(CurrentVerdict);
            CurrentVerdict = verdict;
            var transition = new TargetTransition
            {
                Kind = TransitionSubjectKind.Verdict,
                TargetId = SubjectId,
                TargetName = "Internet",
                From = from,
                To = Map(verdict),
                Timestamp = _time.GetUtcNow(),
                Reason = new TransitionReason
                {
                    Trigger = TransitionTrigger.VerdictChanged,
                    Verdict = verdict.ToString(),
                    Detail = detail,
                },
            };
            _log.LogInformation("Verdict: {Transition}", transition);
            _transitionSubject.OnNext(transition);
        }
    }

    private (VerdictState, string, string? Operator) Classify()
    {
        // Link verdicts name the blamed hop's operator ("Comcast") when tagged.
        static (VerdictState, string, string?) Blame(VerdictState verdict, TargetMonitor monitor, string state)
        {
            var op = monitor.Target.Operator;
            var wording = op is { Length: > 0 }
                ? $"{verdict}: {op} — {monitor.Target.Name} is {state}"
                : $"{verdict}: {monitor.Target.Name} is {state}";
            return (verdict, wording, op);
        }

        var downAncestor = _ancestors.Where(a => a.Monitor.CurrentState == HostState.Down)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (downAncestor.Monitor is not null)
            return Blame(VerdictState.LinkDown, downAncestor.Monitor, "Down");

        var faltAncestor = _ancestors.Where(a => a.Monitor.CurrentState == HostState.Faltering)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (faltAncestor.Monitor is not null)
            return Blame(VerdictState.LinkDegraded, faltAncestor.Monitor, "Faltering");

        var determined = _anchors.Where(a => a.CurrentState
            is not (HostState.Initializing or HostState.Starting)).ToList();
        if (determined.Count == 0)
            return (VerdictState.Unknown, "No anchors determined yet", null);

        var degraded = determined.Where(a => a.CurrentState
            is HostState.Down or HostState.Faltering or HostState.Recovering).ToList();
        string List() => string.Join(", ", degraded.Select(a => $"{a.Target.Name}={a.CurrentState}"));

        // Weather needs INDEPENDENT evidence. Anchors that share an operator
        // ("Microsoft" four times, one per A record) share a path and fail
        // together, so they count once; untagged anchors count individually.
        var determinedUnits = CountOperators(determined);
        var degradedUnits = CountOperators(degraded);
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
                ? $" — all {degraded[0].Target.Operator}" : "";
            return (VerdictState.SiteIncident, $"SiteIncident: {List()}{sharedOperator}{qualifier}", null);
        }
        var counts = grouped
            ? $"{degradedUnits} of {determinedUnits} operators ({degraded.Count} of {determined.Count} anchors)"
            : $"{degraded.Count} of {determined.Count} anchors";
        return (VerdictState.InternetWeather, $"InternetWeather: {counts} degraded ({List()})", null);
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
