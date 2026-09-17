using ConCore.Targets;
using ConEngine.Events;
using System.Net;

namespace ConEngine.Tracking;

/// <summary>
/// Per-host state machine. Derives state from recent ping samples and from
/// topology commands (MarkUnreachable/Release). Pure: every step returns
/// its outcome and never publishes, so the owner decides when to emit.
/// </summary>
internal class HostAnalysisEngine
{
    private readonly PingProfile _profile;
    private readonly TimeProvider _timeProvider;
    private IPAddress _hostAddress;

    public HostAnalysisEngine(PingProfile profile, IPAddress hostAddress, TimeProvider timeProvider)
    {
        _profile = profile;
        _hostAddress = hostAddress;
        _timeProvider = timeProvider;
    }

    public HostState CurrentState { get; private set; } = HostState.Initializing;

    /// <summary>True while running on a seeded (presumed) state that fresh
    /// samples have not yet confirmed.</summary>
    public bool Assumed => _seeded;
    private bool _seeded;

    public DateTimeOffset TrackingStarted { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }

    public int SampleSize { get; private set; }
    public int NumSuccess { get; private set; }
    public int NumFailed { get; private set; }

    public float SuccessRate { get; private set; }
    public float FailureRate { get; private set; }
    public float AverageRoundtripTime { get; private set; }

    /// <summary>DNS moved the host: later statuses carry the new address.</summary>
    public void UpdateAddress(IPAddress address) => _hostAddress = address;

    /// <summary>
    /// Warm restart: adopt a presumed state silently — no transition. Fresh
    /// samples confirm it (nothing emitted) or correct it (one transition
    /// from the seeded state) once MinUsableSample is reached; until then no
    /// threshold transition can fire. Valid only before the first sample.
    /// Returns the status to publish, or null when ignored.
    /// </summary>
    public AnalysisOutcome? Seed(HostState state)
    {
        if (CurrentState != HostState.Initializing)
            return null;
        CurrentState = state;
        _seeded = true;
        TrackingStarted = _timeProvider.GetUtcNow();
        return new AnalysisOutcome(BuildStatus(TrackingStarted), null);
    }

    /// <summary>Commanded by topology: an ancestor is Down, stop deriving state from data.</summary>
    public AnalysisOutcome? MarkUnreachable(Guid ancestorId)
    {
        if (CurrentState == HostState.Unreachable)
            return null;
        _seeded = false;
        return Transition(HostState.Unreachable, new TransitionReason
        {
            Trigger = TransitionTrigger.ParentDown,
            AncestorId = ancestorId,
        });
    }

    /// <summary>Commanded by topology: the ancestor recovered; re-prove from Starting.</summary>
    public AnalysisOutcome? Release()
    {
        if (CurrentState != HostState.Unreachable)
            return null;
        _seeded = false;
        SampleSize = 0; NumSuccess = 0; NumFailed = 0;
        SuccessRate = 0; FailureRate = 0; AverageRoundtripTime = 0;
        return Transition(HostState.Starting, new TransitionReason { Trigger = TransitionTrigger.ParentRecovered });
    }

    /// <summary>Folds the sample window into statistics and state. Returns
    /// null while Unreachable (stale in-flight results must not move a parked host).</summary>
    public AnalysisOutcome? Recalculate(IEnumerable<PingHistoryEntry> samples)
    {
        if (CurrentState == HostState.Unreachable)
            return null;

        SampleSize = 0; NumSuccess = 0; NumFailed = 0;
        float sumRoundtrip = 0f;

        foreach (PingHistoryEntry item in samples)
        {
            if (CurrentState == HostState.Initializing)
            {
                TrackingStarted = item.Timestamp;
                CurrentState = HostState.Starting;
            }
            if (item.Timestamp > LastUpdated)
                LastUpdated = item.Timestamp;

            SampleSize++;
            if (item.Success) NumSuccess++; else NumFailed++;
            sumRoundtrip += item.RoundtripTime;
        }

        SuccessRate = SampleSize > 0 ? (float)NumSuccess / SampleSize : 0f;
        FailureRate = SampleSize > 0 ? (float)NumFailed / SampleSize : 0f;
        AverageRoundtripTime = SampleSize > 0 ? sumRoundtrip / SampleSize : 0f;

        HostState next;
        if (_seeded)
        {
            // A seeded state is a presumption under test: below MinUsableSample
            // no threshold transition may fire (a single lost ping right after
            // boot must not flip a presumed-Responding host); at MinUsableSample
            // classify the fresh sample exactly like a Starting exit.
            if (SampleSize < _profile.MinUsableSample)
            {
                next = CurrentState;
            }
            else
            {
                _seeded = false;
                next = ClassifyFreshSample();
                // A seeded Down that proves alive must exit via Recovering:
                // topology releases the children it parked before the restart
                // only on a Down -> Recovering/Responding transition.
                if (CurrentState == HostState.Down && next != HostState.Down)
                    next = HostState.Recovering;
            }
        }
        else
        {
            next = CurrentState switch
            {
                HostState.Starting when SampleSize >= _profile.MinUsableSample =>
                    ClassifyFreshSample(),
                HostState.Responding when SuccessRate < _profile.FalteringThreshold => HostState.Faltering,
                HostState.Faltering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
                HostState.Faltering when SuccessRate < _profile.DownThreshold => HostState.Down,
                HostState.Down when SuccessRate >= _profile.RecoveringThreshold => HostState.Recovering,
                HostState.Recovering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
                HostState.Recovering when SuccessRate < _profile.DownThreshold => HostState.Down,
                _ => CurrentState,
            };
        }

        if (next != CurrentState)
        {
            return Transition(next, new TransitionReason
            {
                Trigger = TransitionTrigger.ThresholdCrossed,
                SuccessRate = SuccessRate,
                SampleSize = SampleSize,
            });
        }
        return new AnalysisOutcome(BuildStatus(LastUpdated), null);
    }

    private AnalysisOutcome Transition(HostState to, TransitionReason reason)
    {
        var from = CurrentState;
        CurrentState = to;
        var timestamp = reason.Trigger == TransitionTrigger.ThresholdCrossed
            ? LastUpdated
            : _timeProvider.GetUtcNow();
        var status = BuildStatus(timestamp);
        return new AnalysisOutcome(status, new AnalysisTransition
        {
            From = from,
            To = to,
            Timestamp = timestamp,
            Reason = reason,
            Status = status,
        });
    }

    private HostState ClassifyFreshSample() =>
        SuccessRate >= _profile.RespondingThreshold ? HostState.Responding
        : SuccessRate < _profile.DownThreshold ? HostState.Down
        : HostState.Faltering;

    private HostStatus BuildStatus(DateTimeOffset timestamp) => new()
    {
        HostAddress = _hostAddress,
        State = CurrentState,
        Assumed = _seeded,
        TrackingStarted = TrackingStarted,
        SampleSize = SampleSize,
        SuccessRate = SuccessRate,
        FailureRate = FailureRate,
        AverageRoundtripTime = AverageRoundtripTime,
        Timestamp = timestamp,
    };
}
