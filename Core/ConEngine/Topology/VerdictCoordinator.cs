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
    private readonly ReplaySubject<TargetTransition> _transitionSubject = new(bufferSize: 1);

    public VerdictState CurrentVerdict { get; private set; } = VerdictState.Unknown;
    public string? CurrentDetail { get; private set; }
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

    /// <summary>Subscribes to anchors and their ancestors. Call once after monitors exist.</summary>
    public void Start()
    {
        foreach (var m in _anchors.Concat(_ancestors.Select(a => a.Monitor)).Distinct())
            _subscriptions.Add(m.TransitionSource.Subscribe(_ => Recalculate()));
    }

    private void Recalculate()
    {
        lock (_lock)
        {
            var (verdict, detail) = Classify();
            CurrentDetail = detail; // detail carries live counts — always refresh
            if (verdict == CurrentVerdict)
                return;

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

    private (VerdictState, string) Classify()
    {
        var downAncestor = _ancestors.Where(a => a.Monitor.CurrentState == HostState.Down)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (downAncestor.Monitor is not null)
            return (VerdictState.LinkDown, $"LinkDown: {downAncestor.Monitor.Target.Name} is Down");

        var faltAncestor = _ancestors.Where(a => a.Monitor.CurrentState == HostState.Faltering)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (faltAncestor.Monitor is not null)
            return (VerdictState.LinkDegraded, $"LinkDegraded: {faltAncestor.Monitor.Target.Name} is Faltering");

        var determined = _anchors.Where(a => a.CurrentState
            is not (HostState.Initializing or HostState.Starting)).ToList();
        if (determined.Count == 0)
            return (VerdictState.Unknown, "No anchors determined yet");

        var degraded = determined.Where(a => a.CurrentState
            is HostState.Down or HostState.Faltering or HostState.Recovering).ToList();
        string List() => string.Join(", ", degraded.Select(a => $"{a.Target.Name}={a.CurrentState}"));

        // With fewer determined anchors than the weather threshold, weather can
        // never be diagnosed — say so rather than implying full confidence.
        var qualifier = determined.Count < _weatherThreshold
            ? $" (only {determined.Count} anchor(s) — cannot distinguish a site incident from internet weather)"
            : "";

        if (degraded.Count == 0)
            return (VerdictState.Healthy, $"Healthy: {determined.Count} anchors responding{qualifier}");
        if (degraded.Count < _weatherThreshold)
            return (VerdictState.SiteIncident, $"SiteIncident: {List()}{qualifier}");
        return (VerdictState.InternetWeather,
            $"InternetWeather: {degraded.Count} of {determined.Count} anchors degraded ({List()})");
    }

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
