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
    private readonly List<IDisposable> _subscriptions = new();

    private readonly ReplaySubject<TargetTransition> _transitionSubject = new(bufferSize: 1);
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

    public HostState CurrentState { get; private set; } = HostState.Initializing;

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

    public void Start()
    {
        foreach (var member in _members)
            _subscriptions.Add(member.TransitionSource.Subscribe(_ => Recalculate()));
    }

    private void Recalculate()
    {
        lock (_lock)
        {
            var states = _members.Select(m => m.CurrentState).ToList();

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
            var detail = string.Join(", ",
                _members.Select(m => $"{m.Target.Name}={m.CurrentState}"));
            var transition = new TargetTransition
            {
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
            _log.LogInformation("Pool transition: {Transition}", transition);
            _transitionSubject.OnNext(transition);
        }
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        _subscriptions.Clear();
        _transitionSubject.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
