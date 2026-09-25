using System.Collections.Concurrent;
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
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<TargetTransition> _outbox = new();
    private readonly List<IDisposable> _subscriptions = new();

    private readonly Subject<TargetTransition> _transitionSubject = new();
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
            if (states.All(s => s is HostState.Initializing or HostState.Starting))
                next = HostState.Starting;
            else if (states.Any(s => s == HostState.Responding))
                next = HostState.Responding;
            else if (states.All(s => s is HostState.Down or HostState.Unreachable))
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
                    _log.LogInformation("Pool transition: {Transition}", transition);
                    _transitionSubject.OnNext(transition);
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
