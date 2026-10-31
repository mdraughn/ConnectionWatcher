using ConCore.Targets;
using ConEngine.Events;
using System.Net;

namespace ConEngine.Tracking;

/// <summary>
/// Per-host state machine. Its OWN state comes only from this host's
/// samples; topology may add an EXPLANATION — the upstream root cause of an
/// own Down — which shows the host as Unreachable (spec 2026-10-09 §4.4).
/// Samples are always analysed, explained or not. Pure: every step returns
/// its outcome and never publishes, so the owner decides when to emit.
/// </summary>
internal class HostAnalysisEngine
{
    /// <summary>Seed(Unreachable): an upstream cause topology has not named yet.</summary>
    private static readonly Guid SeededCause = Guid.Empty;

    private readonly PingProfile _profile;
    private readonly TimeProvider _timeProvider;
    private IPAddress _hostAddress;
    private HostState _own = HostState.Initializing;
    private Guid? _explainedBy;
    // The cause this host was released from: stamped on its first threshold
    // transition out of Starting so notifiers can treat that transition as
    // part of the upstream incident. Cleared after that transition, or when
    // topology names a cause again.
    private Guid? _releasedFrom;

    public HostAnalysisEngine(PingProfile profile, IPAddress hostAddress, TimeProvider timeProvider)
    {
        _profile = profile;
        _hostAddress = hostAddress;
        _timeProvider = timeProvider;
    }

    /// <summary>What everyone sees: Unreachable = own Down + an upstream root cause.</summary>
    public HostState CurrentState => Show(_own, _explainedBy);

    /// <summary>The root cause while shown Unreachable; null otherwise —
    /// including a seeded Unreachable whose cause topology has not named yet.</summary>
    public Guid? ExplainedBy =>
        CurrentState == HostState.Unreachable && _explainedBy != SeededCause ? _explainedBy : null;

    private static HostState Show(HostState own, Guid? explainedBy) =>
        own == HostState.Down && explainedBy is not null ? HostState.Unreachable : own;

    /// <summary>True while running on a seeded (presumed) state that fresh
    /// samples have not yet confirmed.</summary>
    public bool Assumed => _seeded;
    private bool _seeded;

    public DateTimeOffset TrackingStarted { get; private set; }
    private DateTimeOffset LastUpdated { get; set; }

    public int SampleSize { get; private set; }

    public double SuccessRate { get; private set; }
    public double FailureRate { get; private set; }
    public double AverageRoundtripTime { get; private set; }

    /// <summary>DNS moved the host: later statuses carry the new address.</summary>
    public void UpdateAddress(IPAddress address) => _hostAddress = address;

    /// <summary>
    /// Warm restart: adopt a presumed state silently — no transition. Fresh
    /// samples confirm it (nothing emitted) or correct it (one transition
    /// from the seeded state) once MinUsableSample is reached; until then no
    /// threshold transition can fire. Unreachable seeds as own Down with a
    /// placeholder cause that topology's first explanation replaces.
    /// Valid only before the first sample. Returns the status to publish, or
    /// null when ignored.
    /// </summary>
    public AnalysisOutcome? Seed(HostState state)
    {
        if (_own != HostState.Initializing)
            return null;
        if (state == HostState.Unreachable)
        {
            _own = HostState.Down;
            _explainedBy = SeededCause;
        }
        else
        {
            _own = state;
        }
        _seeded = true;
        TrackingStarted = _timeProvider.GetUtcNow();
        return new AnalysisOutcome(BuildStatus(TrackingStarted), null);
    }

    /// <summary>
    /// Topology names (or withdraws) the upstream root cause. It only shows
    /// when this host's own evidence says Down; an explanation never makes a
    /// healthy host Unreachable. Withdrawing the explanation of a host shown
    /// Unreachable RELEASES it (see <see cref="Release"/>): its window is
    /// from the outage, so it re-proves on fresh evidence instead of turning
    /// Down "no longer explained" — whatever state the old cause is in.
    /// </summary>
    public AnalysisOutcome? SetExplanation(Guid? ancestorId)
    {
        if (ancestorId is null && CurrentState == HostState.Unreachable)
            return Release();
        if (ancestorId is not null)
            _releasedFrom = null; // a new incident: the old one's recovery is over
        var before = CurrentState;
        _explainedBy = ancestorId;
        var after = CurrentState;
        if (after == before)
            return null;
        // Only Down -> Unreachable remains: a withdrawal from Unreachable released above.
        return Emit(before, after, new TransitionReason
        {
            Trigger = TransitionTrigger.ParentDown, AncestorId = ancestorId,
            SuccessRate = SuccessRate, SampleSize = SampleSize,
        }, _timeProvider.GetUtcNow());
    }

    /// <summary>Commanded by topology: the explanation is gone (the root cause
    /// recovered, or forwarding past it was proven); clear the statistics and
    /// re-prove from Starting. The first threshold transition out of Starting
    /// will name the former cause.</summary>
    public AnalysisOutcome? Release()
    {
        if (CurrentState != HostState.Unreachable)
            return null;
        var formerCause = _explainedBy == SeededCause ? null : _explainedBy;
        _seeded = false;
        SampleSize = 0;
        SuccessRate = 0; FailureRate = 0; AverageRoundtripTime = 0;
        _own = HostState.Starting;
        _explainedBy = null;
        _releasedFrom = formerCause;
        return Emit(HostState.Unreachable, HostState.Starting,
            new TransitionReason { Trigger = TransitionTrigger.ParentRecovered, AncestorId = formerCause },
            _timeProvider.GetUtcNow());
    }

    /// <summary>The owning monitor released this host before tracking began
    /// (a seeded Unreachable awaiting DNS): remember the former cause for the
    /// first transition out of Starting. Valid only before the first sample.</summary>
    public void AdoptRelease(Guid? formerCause)
    {
        if (_own == HostState.Initializing)
            _releasedFrom = formerCause;
    }

    /// <summary>Folds the sample window into statistics and state — always,
    /// explained or not.</summary>
    public AnalysisOutcome Recalculate(IEnumerable<PingHistoryEntry> samples)
    {
        SampleSize = 0;
        int numSuccess = 0, numFailed = 0;
        double sumRoundtrip = 0d;

        foreach (PingHistoryEntry item in samples)
        {
            if (_own == HostState.Initializing)
            {
                TrackingStarted = item.Timestamp;
                _own = HostState.Starting;
            }
            if (item.Timestamp > LastUpdated)
                LastUpdated = item.Timestamp;

            SampleSize++;
            if (item.Success)
            {
                numSuccess++;
                sumRoundtrip += item.RoundtripTime;
            }
            else
            {
                numFailed++;
            }
        }

        SuccessRate = SampleSize > 0 ? (double)numSuccess / SampleSize : 0d;
        FailureRate = SampleSize > 0 ? (double)numFailed / SampleSize : 0d;
        // Answered pings only: a loss carries 0 ms and would drag the mean down.
        AverageRoundtripTime = numSuccess > 0 ? sumRoundtrip / numSuccess : 0d;

        HostState next;
        if (_seeded)
        {
            // A seeded state is a presumption under test: below MinUsableSample
            // no threshold transition may fire (a single lost ping right after
            // boot must not flip a presumed-Responding host); at MinUsableSample
            // classify the fresh sample exactly like a Starting exit.
            if (SampleSize < _profile.MinUsableSample)
            {
                next = _own;
            }
            else
            {
                _seeded = false;
                next = ClassifyFreshSample();
                // A seeded Down that proves alive exits via Recovering, like
                // any Down: notifiers and History see a recovery, and topology
                // bursts everything beyond this hop on Down -> Recovering.
                if (_own == HostState.Down && next != HostState.Down)
                    next = HostState.Recovering;
            }
        }
        else
        {
            next = _own switch
            {
                HostState.Starting when SampleSize >= _profile.MinUsableSample =>
                    ClassifyFreshSample(),
                HostState.Responding when SuccessRate < _profile.FalteringThreshold => HostState.Faltering,
                HostState.Faltering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
                HostState.Faltering when SuccessRate < _profile.DownThreshold => HostState.Down,
                HostState.Down when SuccessRate >= _profile.RecoveringThreshold => HostState.Recovering,
                HostState.Recovering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
                HostState.Recovering when SuccessRate < _profile.DownThreshold => HostState.Down,
                _ => _own,
            };
        }

        var before = CurrentState;
        _own = next;
        var after = CurrentState;
        if (after != before)
        {
            var reason = after == HostState.Unreachable
                ? new TransitionReason
                {
                    Trigger = TransitionTrigger.ParentDown, AncestorId = _explainedBy,
                    SuccessRate = SuccessRate, SampleSize = SampleSize,
                }
                : new TransitionReason
                {
                    Trigger = TransitionTrigger.ThresholdCrossed,
                    SuccessRate = SuccessRate, SampleSize = SampleSize,
                };
            if (before == HostState.Starting)
            {
                // A released host's first verdict on its own evidence belongs
                // to the upstream incident it was released from (I1).
                if (reason.Trigger == TransitionTrigger.ThresholdCrossed && _releasedFrom is { } former)
                    reason = reason with { AncestorId = former };
                _releasedFrom = null;
            }
            return Emit(before, after, reason, LastUpdated);
        }
        return new AnalysisOutcome(BuildStatus(LastUpdated), null);
    }

    private AnalysisOutcome Emit(HostState from, HostState to, TransitionReason reason, DateTimeOffset timestamp)
    {
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
