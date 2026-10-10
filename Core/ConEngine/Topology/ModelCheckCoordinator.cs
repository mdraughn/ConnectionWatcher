using System.Collections.Concurrent;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
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
    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<ModelConditionChange> _outbox = new();
    private readonly Subject<ModelConditionChange> _changes = new();
    private readonly List<IDisposable> _subscriptions = new();

    // Guarded by _lock.
    private readonly Dictionary<string, ModelCondition> _active = new();
    private readonly Dictionary<Guid, DateTimeOffset> _falteringSince = new();
    private DateTimeOffset _startedUtc;
    private ITimer? _timer;
    private bool _disposed;

    public ModelCheckCoordinator(TargetGraph graph, IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        bool unprovableUp, TimeProvider timeProvider, ILogger<ModelCheckCoordinator> log)
    {
        _monitors = monitors;
        _unprovableUp = unprovableUp;
        _time = timeProvider;
        _log = log;
        foreach (var id in monitors.Keys)
        {
            // GetDescendants is breadth-first: the first answering one is the nearest witness.
            var beyond = graph.GetDescendants(id).Select(d => d.Id).Where(monitors.ContainsKey).ToList();
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
                if (state is not (HostState.Down or HostState.Faltering))
                    continue;
                var witnessId = beyond.FirstOrDefault(d => TopologyCoordinator.Answering(states[d]));
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
                else if (now - _falteringSince[id] >= FalteringHold)
                {
                    Add(found, ModelWarningKind.HopBypassed, hop.Target,
                        $"{hop.Target.Name} is losing replies to its probe ({probe}), but {witness} beyond it answers{BypassTail}");
                }
            }

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
        DrainOutbox();
    }

    private static void Add(Dictionary<string, ModelCondition> found, ModelWarningKind kind,
        Target? target, string message)
    {
        var key = ModelCondition.KeyFor(kind, target?.Id);
        found[key] = new ModelCondition(key, kind, target?.Id, target?.Name, message);
    }

    /// <summary>"tcp/80" for a TCP probe, "ping" for ICMP echo.</summary>
    private static string ProbeText(Target target)
    {
        ProbeSpec.TryParse(target.Probe, out var probe);
        return probe.Kind == ProbeKind.Tcp ? $"tcp/{probe.Port}" : "ping";
    }

    /// <summary>Same discipline as VerdictCoordinator: TryEnter, so a thread
    /// that finds another draining leaves its items to that thread.</summary>
    private void DrainOutbox()
    {
        while (!_outbox.IsEmpty)
        {
            if (!_publishLock.TryEnter())
                return;
            try
            {
                while (_outbox.TryDequeue(out var change))
                {
                    _log.LogDebug("Model condition {Key} {State}", change.Condition.Key,
                        change.Active ? "raised" : "cleared");
                    _changes.OnNext(change);
                }
            }
            finally
            {
                _publishLock.Exit();
            }
        }
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
