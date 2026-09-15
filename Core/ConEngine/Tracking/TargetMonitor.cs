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
/// </summary>
public class TargetMonitor : IDisposable
{
    /// <summary>Refused-with-no-accept samples before a TCP probe is flagged persistently refused.</summary>
    public const int RefusalWarningSamples = 200;

    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TargetMonitor> _log;
    private readonly Lock _lock = new();

    private long _acceptedReplies;
    private long _refusedReplies;
    private long _pingCount;

    private HostTracker? _tracker;
    private IDisposable? _transitionSubscription;
    private IDisposable? _statusSubscription;

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
                _tracker.Seed(seed);

            _transitionSubscription = _tracker.HostStateChangeSource.Subscribe(OnAnalysisTransition);
            _statusSubscription = _tracker.HostStatusSource.Subscribe(status =>
            {
                var targetStatus = new TargetStatus
                {
                    TargetId = Target.Id, TargetName = Target.Name, Host = status,
                };
                LastStatus = targetStatus;
                _statusSubject.OnNext(targetStatus);
            });

            // Honor topology commands that arrived before we started.
            if (_pendingUnreachableAncestor is { } ancestor)
                _tracker.MarkUnreachable(ancestor);
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
        lock (_lock) { _tracker?.RecordPingResult(result); }
    }

    public void SetUnreachable(Guid ancestorId)
    {
        lock (_lock)
        {
            if (_tracker is not null)
            {
                _tracker.MarkUnreachable(ancestorId);
                return;
            }
            if (_preTrackerState == HostState.Unreachable)
                return;
            _seedState = null; // topology has spoken; the presumption is void
            _pendingUnreachableAncestor = ancestorId;
            EmitPreTrackerTransition(HostState.Unreachable, new TransitionReason
            {
                Trigger = TransitionTrigger.ParentDown, AncestorId = ancestorId,
            });
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_tracker is not null)
            {
                _tracker.Release();
                return;
            }
            if (_preTrackerState != HostState.Unreachable)
                return;
            _seedState = null; // released: re-prove from Starting, not the presumption
            _pendingUnreachableAncestor = null;
            EmitPreTrackerTransition(HostState.Starting,
                new TransitionReason { Trigger = TransitionTrigger.ParentRecovered });
        }
    }

    /// <summary>Reports a DNS resolution failure. Emits one transition; later failures are quiet.</summary>
    public void ReportResolutionFailure(string detail)
    {
        lock (_lock)
        {
            if (_resolutionFailureReported || _tracker is not null)
                return;
            _resolutionFailureReported = true;
            EmitPreTrackerTransition(HostState.Starting, new TransitionReason
            {
                Trigger = TransitionTrigger.ResolutionFailed, Detail = detail,
            });
        }
    }

    private void EmitPreTrackerTransition(HostState to, TransitionReason reason)
    {
        var from = _preTrackerState;
        _preTrackerState = to;
        Discipline.OnStateChanged(to);
        _transitionSubject.OnNext(new TargetTransition
        {
            TargetId = Target.Id,
            TargetName = Target.Name,
            From = from,
            To = to,
            Timestamp = _timeProvider.GetUtcNow(),
            Reason = reason,
        });
    }

    private void OnAnalysisTransition(AnalysisTransition at)
    {
        // Compute burst loss BEFORE notifying the discipline (which may reset the window).
        var reason = at.Reason;
        if (Discipline.BurstStartedAt is { } burstStart && _tracker is not null)
        {
            var entries = _tracker.EntriesSince(burstStart).ToList();
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
        _log.LogInformation("Transition: {Transition}", transition);
        _transitionSubject.OnNext(transition);
    }

    public void Dispose()
    {
        _transitionSubscription?.Dispose();
        _statusSubscription?.Dispose();
        _transitionSubject.OnCompleted();
        _statusSubject.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
