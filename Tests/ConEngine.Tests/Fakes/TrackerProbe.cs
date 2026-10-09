using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;

namespace ConEngine.Tests.Fakes;

/// <summary>
/// Drives a <see cref="HostTracker"/> the way TargetMonitor does and keeps
/// every transition its commands RETURN — what the tracker's old
/// HostStateChangeSource subscription used to collect.
/// </summary>
public sealed class TrackerProbe(IPAddress address, PingProfile profile, TimeProvider time)
{
    private readonly HostTracker _tracker = new(address, profile, time);

    public List<AnalysisTransition> Transitions { get; } = [];
    public HostState CurrentState => _tracker.CurrentState;
    public bool Assumed => _tracker.Assumed;
    public int HistoryCount => _tracker.HistoryCount;

    public void Seed(HostState state) => Keep(_tracker.Seed(state));
    public void RecordPingResult(PingResult result) => Keep(_tracker.RecordPingResult(result));
    public void SetExplanation(Guid? ancestorId) => Keep(_tracker.SetExplanation(ancestorId));
    public Guid? ExplainedBy => _tracker.ExplainedBy;
    public void Release() => Keep(_tracker.Release());
    public IReadOnlyList<PingHistoryEntry> EntriesSince(DateTimeOffset since) => _tracker.EntriesSince(since);

    private void Keep(AnalysisOutcome? outcome)
    {
        if (outcome?.Transition is { } transition)
            Transitions.Add(transition);
    }
}
