using ConEngine.Tracking;

namespace ConEngine.Events;

/// <summary>What caused a state transition.</summary>
public enum TransitionTrigger
{
    ThresholdCrossed,
    ParentDown,
    ParentRecovered,
    ResolutionFailed,
    PoolMembersChanged
}

/// <summary>Structured evidence behind a transition.</summary>
public record TransitionReason
{
    public required TransitionTrigger Trigger { get; init; }
    public double? SuccessRate { get; init; }
    public int? SampleSize { get; init; }
    /// <summary>Loss rate measured over the current burst window, if a burst was active.</summary>
    public double? BurstLossRate { get; init; }
    /// <summary>The ancestor target responsible, for ParentDown/ParentRecovered.</summary>
    public Guid? AncestorId { get; init; }
    public string? Detail { get; init; }

    public override string ToString()
    {
        var parts = new List<string> { Trigger.ToString() };
        if (SuccessRate is { } sr) parts.Add($"success {sr:P0}");
        if (SampleSize is { } n) parts.Add($"over {n} pings");
        if (BurstLossRate is { } lr) parts.Add($"burst loss {lr:P0}");
        if (Detail is { Length: > 0 }) parts.Add(Detail);
        return string.Join("; ", parts);
    }
}

/// <summary>A host-level state transition emitted by the analysis engine.</summary>
public record AnalysisTransition
{
    public required HostState From { get; init; }
    public required HostState To { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required TransitionReason Reason { get; init; }
    public required HostStatus Status { get; init; }
}

/// <summary>A target-level state transition: the event the whole system revolves around.</summary>
public record TargetTransition
{
    public required Guid TargetId { get; init; }
    public required string TargetName { get; init; }
    public required HostState From { get; init; }
    public required HostState To { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required TransitionReason Reason { get; init; }
    public float AverageRoundtripTime { get; init; }

    public override string ToString() => $"{TargetName}: {From} -> {To} ({Reason})";
}

/// <summary>Periodic status for a target (statistics snapshot, not a transition).</summary>
public record TargetStatus
{
    public required Guid TargetId { get; init; }
    public required string TargetName { get; init; }
    public required HostStatus Host { get; init; }
}
