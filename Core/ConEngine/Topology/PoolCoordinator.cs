using System.Reactive.Subjects;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Derives an aggregate state for a rotation pool from its members and emits
/// pool-level transitions on the shared TargetTransition shape.
/// </summary>
public class PoolCoordinator : IDisposable
{
    private readonly PoolDefinition _pool;
    private readonly IReadOnlyList<TargetMonitor> _members;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PoolCoordinator> _log;
    private readonly Lock _lock = new();
    private readonly OrderedOutbox<TargetTransition> _outbox;
    private readonly List<IDisposable> _subscriptions = new();

    private readonly Subject<TargetTransition> _transitionSubject = new();
    /// <summary>Transitions this coordinator raises as its own state changes. A
    /// queued transition may be delivered on another thread after the
    /// triggering call has already returned. If a subscriber throws, any
    /// transitions still queued behind it are delivered with the coordinator's
    /// next transition.</summary>
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

    public HostState CurrentState { get; private set; } = HostState.Initializing;

    /// <summary>The pool this coordinator aggregates.</summary>
    public PoolDefinition Pool => _pool;

    public PoolCoordinator(PoolDefinition pool, IReadOnlyList<TargetMonitor> members,
        TimeProvider timeProvider, ILogger<PoolCoordinator> log)
    {
        _pool = pool;
        _members = members;
        _timeProvider = timeProvider;
        _log = log;
        _outbox = new OrderedOutbox<TargetTransition>(t =>
        {
            _log.LogInformation("Pool transition: {Transition}", t);
            _transitionSubject.OnNext(t);
        }, OutboxMode.Skip);
    }

    /// <summary>Stagger offset for member k of n at the given normal interval.</summary>
    public static TimeSpan PhaseOffset(int memberIndex, int memberCount, TimeSpan interval) =>
        interval * memberIndex / memberCount;

    /// <summary>Warm restart: adopt a presumed aggregate state silently — no
    /// transition. Member transitions recalculate (and correct) it as usual.</summary>
    public void Seed(HostState state)
    {
        lock (_lock)
        {
            if (CurrentState == HostState.Initializing)
                CurrentState = state;
        }
    }

    public void Start()
    {
        foreach (var member in _members)
            _subscriptions.Add(member.TransitionSource.Subscribe(_ => Recalculate()));
    }

    private void Recalculate()
    {
        lock (_lock)
        {
            var members = _members.Select(m => (m.Target.Name, State: m.CurrentState)).ToList();
            var states = members.Select(m => m.State).ToList();

            HostState next;
            if (states.All(s => s.IsUndetermined()))
                next = HostState.Starting;
            else if (states.Any(s => s == HostState.Responding))
                next = HostState.Responding;
            else if (states.All(s => s.IsFailed()))
                next = HostState.Down;
            else
                next = HostState.Faltering;

            if (next == CurrentState)
                return;

            var from = CurrentState;
            CurrentState = next;
            var detail = string.Join(", ", members.Select(m => $"{m.Name}={m.State}"));
            var transition = new TargetTransition
            {
                Kind = TransitionSubjectKind.Pool,
                TargetId = _pool.Id,
                TargetName = _pool.Name,
                From = from,
                To = next,
                Timestamp = _timeProvider.GetUtcNow(),
                Reason = new TransitionReason
                {
                    Trigger = TransitionTrigger.PoolMembersChanged,
                    Detail = detail,
                },
            };
            // Queued under the lock (queue order is state order), published after it.
            _outbox.Enqueue(transition);
        }
        _outbox.Drain();
    }

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
