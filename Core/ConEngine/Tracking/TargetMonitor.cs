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
/// The HostTracker it owns is pure; this monitor's _lock is the only lock around it.
/// </summary>
public class TargetMonitor : IDisposable
{
    /// <summary>Refused-with-no-accept samples before a TCP probe is flagged persistently refused.</summary>
    public const int RefusalWarningSamples = 200;

    private readonly TimeProvider _timeProvider;
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
    private Guid? _explanation; // topology's latest word, applied once tracking starts
    private bool _explanationGiven; // topology has spoken (null is a word too)
    private Guid? _preTrackerReleasedFrom; // released before tracking: handed to the tracker
    private bool _resolutionFailureReported;
    private HostState? _seedState;

    // A plain subject: every subscriber attaches before the engine activates anything, and LastStatus serves polling consumers.
    private readonly Subject<TargetTransition> _transitionSubject = new();

    public TargetMonitor(Target target, PingProfile profile, PingDiscipline discipline,
        TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        Target = target;
        Profile = profile;
        Discipline = discipline;
        _timeProvider = timeProvider;
        _log = loggerFactory.CreateLogger<TargetMonitor>();
    }

    public Target Target { get; }
    public PingProfile Profile { get; }
    public PingDiscipline Discipline { get; }
    public IPAddress? ResolvedAddress { get; private set; }

    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

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
            _tracker = new HostTracker(resolvedAddress, Profile, _timeProvider);
            if (_seedState is { } seed)
                QueueOutcomeLocked(_tracker.Seed(seed));
            if (_preTrackerReleasedFrom is { } former)
                _tracker.AdoptRelease(former);

            // Honor the topology's explanation if it spoke before we started.
            // A null still matters: it withdraws a seeded Unreachable's placeholder cause.
            if (_explanationGiven)
                QueueOutcomeLocked(_tracker.SetExplanation(_explanation));
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

    /// <summary>Topology names (or withdraws) the upstream root cause. Only an
    /// own Down shows it (as Unreachable); before tracking it is remembered.</summary>
    /// <remarks>
    /// Withdrawing it (null) from a monitor shown Unreachable RELEASES the
    /// monitor — history cleared, back to Starting, burst — checked and done
    /// atomically under <c>_lock</c>, so topology never acts on a stale read
    /// of this monitor (final review I1/I4). Returns true when this call
    /// released the monitor.
    /// </remarks>
    public bool SetExplanation(Guid? ancestorId)
    {
        bool released;
        lock (_lock)
        {
            if (_tracker is not null)
            {
                _explanation = ancestorId;
                _explanationGiven = true;
                var outcome = _tracker.SetExplanation(ancestorId);
                released = outcome?.Transition is { Reason.Trigger: TransitionTrigger.ParentRecovered };
                QueueOutcomeLocked(outcome);
            }
            else if (ancestorId is null && _preTrackerState == HostState.Unreachable)
            {
                released = true;
                ReleasePreTrackerLocked();
            }
            else
            {
                _explanation = ancestorId;
                _explanationGiven = true;
                released = false;
            }
        }
        DrainOutbox();
        return released;
    }

    /// <summary>The root cause while shown Unreachable; null otherwise. Before
    /// tracking (a seeded Unreachable awaiting DNS) it is topology's latest word.</summary>
    public Guid? ExplainedBy
    {
        get
        {
            lock (_lock)
            {
                if (_tracker is not null)
                    return _tracker.ExplainedBy;
                return _preTrackerState == HostState.Unreachable ? _explanation : null;
            }
        }
    }

    /// <summary>Releases an Unreachable monitor to re-prove from Starting
    /// (no-op otherwise). Topology releases through SetExplanation(null).</summary>
    public void Release()
    {
        lock (_lock)
        {
            if (_tracker is not null)
            {
                _explanation = null;
                QueueOutcomeLocked(_tracker.Release());
            }
            else if (_preTrackerState == HostState.Unreachable)
            {
                ReleasePreTrackerLocked();
            }
        }
        DrainOutbox();
    }

    /// <summary>A seeded Unreachable released before DNS resolved: re-prove
    /// from Starting, not the presumption. Called under _lock.</summary>
    private void ReleasePreTrackerLocked()
    {
        var formerCause = _explanation;
        _seedState = null;
        _explanation = null;
        _explanationGiven = true;
        _preTrackerReleasedFrom = formerCause;
        QueuePreTrackerTransitionLocked(HostState.Starting,
            new TransitionReason { Trigger = TransitionTrigger.ParentRecovered, AncestorId = formerCause });
        Discipline.ForceBurst();
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

        LastStatus = new TargetStatus
        {
            TargetId = Target.Id, TargetName = Target.Name, Host = outcome.Status,
        };

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
        if (reason.Trigger == TransitionTrigger.ParentRecovered)
            Discipline.ForceBurst(); // released: reach it NOW so it re-proves within seconds

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
        GC.SuppressFinalize(this);
    }
}
