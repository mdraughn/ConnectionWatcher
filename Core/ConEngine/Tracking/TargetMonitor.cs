using System.Collections.Concurrent;
using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Scheduling;
using Microsoft.Extensions.Logging;

namespace ConEngine.Tracking;

/// <summary>
/// Binds one Target to its tracker and discipline, and enriches host-level
/// transitions into target-level TargetTransition events.
///
/// Threading contract: state changes happen under <c>_lock</c>; every
/// publication is queued while the lock is held (so delivery order equals
/// state order) and delivered only after it is released, serialized through
/// <c>_publishLock</c>. Subscribers may therefore take other monitors' locks
/// freely — nothing they can reach is held while they run.
/// </summary>
public class TargetMonitor : IDisposable
{
    /// <summary>Refused-with-no-accept samples before a TCP probe is flagged persistently refused.</summary>
    public const int RefusalWarningSamples = 200;

    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TargetMonitor> _log;
    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<Action> _outbox = new();

    private long _acceptedReplies;
    private long _refusedReplies;
    private long _pingCount;

    private HostTracker? _tracker;

    // Pre-tracker state bookkeeping (before StartTracking / for resolution failures).
    private HostState _preTrackerState = HostState.Initializing;
    private Guid? _pendingUnreachableAncestor;
    private bool _resolutionFailureReported;
    private HostState? _seedState;

    private readonly ReplaySubject<TargetTransition> _transitionSubject = new(bufferSize: 1);
    private readonly ReplaySubject<TargetStatus> _statusSubject = new(bufferSize: 1);

    public TargetMonitor(Target target, PingProfile profile, PingDiscipline discipline,
        TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        Target = target;
        Profile = profile;
        Discipline = discipline;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<TargetMonitor>();
    }

    public Target Target { get; }
    public PingProfile Profile { get; }
    public PingDiscipline Discipline { get; }
    public IPAddress? ResolvedAddress { get; private set; }

    public IObservable<TargetTransition> TransitionSource => _transitionSubject;
    public IObservable<TargetStatus> StatusSource => _statusSubject;

    /// <summary>Most recent status snapshot, for polling consumers (e.g. /status).</summary>
    public TargetStatus? LastStatus { get; private set; }

    public HostState CurrentState
    {
        get { lock (_lock) { return _tracker?.CurrentState ?? _preTrackerState; } }
    }

    /// <summary>True while running on an unconfirmed warm-restart presumption.</summary>
    public bool IsAssumed
    {
        get { lock (_lock) { return _tracker?.Assumed ?? _seedState is not null; } }
    }

    /// <summary>Successful pings/connects whose message was not "Refused" (ICMP replies, accepted TCP connects).</summary>
    public long AcceptedReplies => Interlocked.Read(ref _acceptedReplies);

    /// <summary>Successful TCP connects whose message was "Refused" (port closed, nothing listening).</summary>
    public long RefusedReplies => Interlocked.Read(ref _refusedReplies);

    /// <summary>True once this monitor has seen only refusals, never an accept — a probe pointed at a closed port.</summary>
    public bool PersistentlyRefused => AcceptedReplies == 0 && RefusedReplies >= RefusalWarningSamples;

    /// <summary>Every result ever recorded, success or not — the dashboard's
    /// activity LEDs flicker when this grows between snapshots.</summary>
    public long PingCount => Interlocked.Read(ref _pingCount);

    /// <summary>
    /// Warm restart: adopt a presumed state silently — no transition. Shows
    /// immediately (marked assumed); applied to the tracker at StartTracking
    /// and confirmed or corrected by fresh samples. Valid only before any
    /// tracking or topology command has touched this monitor.
    /// </summary>
    public void Seed(HostState state)
    {
        lock (_lock)
        {
            if (_tracker is not null || _preTrackerState != HostState.Initializing)
                return;
            _seedState = state;
            _preTrackerState = state;
            Discipline.OnStateChanged(state);
        }
    }

    /// <summary>Creates the tracker once the address is known. Idempotent.</summary>
    public void StartTracking(IPAddress resolvedAddress)
    {
        lock (_lock)
        {
            if (_tracker is not null)
                return;

            ResolvedAddress = resolvedAddress;
            _tracker = new HostTracker(resolvedAddress, Profile, _timeProvider,
                _loggerFactory.CreateLogger<HostTracker>());
            if (_seedState is { } seed)
                QueueOutcomeLocked(_tracker.Seed(seed));

            // Honor topology commands that arrived before we started.
            if (_pendingUnreachableAncestor is { } ancestor)
                QueueOutcomeLocked(_tracker.MarkUnreachable(ancestor));
        }
        DrainOutbox();
    }

    /// <summary>DNS moved the target: keep state and history, change the address.</summary>
    public void Repoint(IPAddress newAddress)
    {
        lock (_lock)
        {
            ResolvedAddress = newAddress;
            _tracker?.UpdateAddress(newAddress);
        }
    }

    public void RecordPingResult(PingResult result)
    {
        Interlocked.Increment(ref _pingCount);
        if (result.Success)
        {
            if (result.Message == "Refused") Interlocked.Increment(ref _refusedReplies);
            else Interlocked.Increment(ref _acceptedReplies);
        }
        lock (_lock)
        {
            if (_tracker is not null)
                QueueOutcomeLocked(_tracker.RecordPingResult(result));
        }
        DrainOutbox();
    }

    public void SetUnreachable(Guid ancestorId)
    {
        lock (_lock)
        {
            if (_tracker is not null)
            {
                QueueOutcomeLocked(_tracker.MarkUnreachable(ancestorId));
            }
            else if (_preTrackerState != HostState.Unreachable)
            {
                _seedState = null; // topology has spoken; the presumption is void
                _pendingUnreachableAncestor = ancestorId;
                QueuePreTrackerTransitionLocked(HostState.Unreachable, new TransitionReason
                {
                    Trigger = TransitionTrigger.ParentDown, AncestorId = ancestorId,
                });
            }
        }
        DrainOutbox();
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_tracker is not null)
            {
                QueueOutcomeLocked(_tracker.Release());
            }
            else if (_preTrackerState == HostState.Unreachable)
            {
                _seedState = null; // released: re-prove from Starting, not the presumption
                _pendingUnreachableAncestor = null;
                QueuePreTrackerTransitionLocked(HostState.Starting,
                    new TransitionReason { Trigger = TransitionTrigger.ParentRecovered });
            }
        }
        DrainOutbox();
    }

    /// <summary>Reports a DNS resolution failure. Emits one transition; later failures are quiet.</summary>
    public void ReportResolutionFailure(string detail)
    {
        lock (_lock)
        {
            if (_resolutionFailureReported || _tracker is not null)
                return;
            _resolutionFailureReported = true;
            QueuePreTrackerTransitionLocked(HostState.Starting, new TransitionReason
            {
                Trigger = TransitionTrigger.ResolutionFailed, Detail = detail,
            });
        }
        DrainOutbox();
    }

    private void QueuePreTrackerTransitionLocked(HostState to, TransitionReason reason)
    {
        var from = _preTrackerState;
        _preTrackerState = to;
        Discipline.OnStateChanged(to);
        var transition = new TargetTransition
        {
            TargetId = Target.Id,
            TargetName = Target.Name,
            From = from,
            To = to,
            Timestamp = _timeProvider.GetUtcNow(),
            Reason = reason,
        };
        _outbox.Enqueue(() => _transitionSubject.OnNext(transition));
    }

    /// <summary>Turns a tracker outcome into target-level publications. Called under _lock.</summary>
    private void QueueOutcomeLocked(AnalysisOutcome? outcome)
    {
        if (outcome is null)
            return;

        var targetStatus = new TargetStatus
        {
            TargetId = Target.Id, TargetName = Target.Name, Host = outcome.Status,
        };
        LastStatus = targetStatus;
        _outbox.Enqueue(() => _statusSubject.OnNext(targetStatus));

        if (outcome.Transition is not { } at)
            return;

        // Compute burst loss BEFORE notifying the discipline (which may reset the window).
        var reason = at.Reason;
        if (Discipline.BurstStartedAt is { } burstStart && _tracker is not null)
        {
            var entries = _tracker.EntriesSince(burstStart);
            if (entries.Count > 0)
                reason = reason with
                {
                    BurstLossRate = 1.0 - entries.Count(e => e.Success) / (double)entries.Count,
                };
        }

        Discipline.OnStateChanged(at.To);

        var transition = new TargetTransition
        {
            TargetId = Target.Id,
            TargetName = Target.Name,
            From = at.From,
            To = at.To,
            Timestamp = at.Timestamp,
            Reason = reason,
            AverageRoundtripTime = at.Status.AverageRoundtripTime,
        };
        _outbox.Enqueue(() =>
        {
            _log.LogInformation("Transition: {Transition}", transition);
            _transitionSubject.OnNext(transition);
        });
    }

    /// <summary>Delivers queued publications in order, never under _lock.</summary>
    private void DrainOutbox()
    {
        if (_outbox.IsEmpty)
            return;
        lock (_publishLock)
        {
            while (_outbox.TryDequeue(out var publish))
                publish();
        }
    }

    public void Dispose()
    {
        _transitionSubject.OnCompleted();
        _statusSubject.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
