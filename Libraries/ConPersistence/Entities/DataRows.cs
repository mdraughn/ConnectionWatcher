namespace ConPersistence.Entities;

/// <summary>A recorded state transition with its evidence. Pruned after
/// Retention.TransitionMonths (default 24), except each subject's most
/// recent row, which warm restart seeds from.</summary>
public class TransitionRow
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = "";
    public string SubjectKind { get; set; } = "Target";
    public string FromState { get; set; } = "";
    public string ToState { get; set; } = "";
    public string Trigger { get; set; } = "";
    public double? SuccessRate { get; set; }
    public int? SampleSize { get; set; }
    public double? BurstLossRate { get; set; }
    public Guid? AncestorId { get; set; }
    public string? Detail { get; set; }
    public string? Verdict { get; set; }
    public float AvgRtt { get; set; }
}

/// <summary>A sub-Faltering loss episode: trouble too small to move the state
/// machine, recorded separately from outages. Pruned with minute rollups.</summary>
public class GlitchRow
{
    public long Id { get; set; }
    public Guid TargetId { get; set; }
    public string TargetName { get; set; } = "";
    public int Class { get; set; } // 0 = Target, 1 = Internet
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public int LostCount { get; set; }
    public int SampleCount { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Aggregated ping statistics for one target over one period.</summary>
public class RollupRow
{
    public long Id { get; set; }
    public Guid TargetId { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public string Resolution { get; set; } = "Minute";
    public int PingCount { get; set; }
    public int SuccessCount { get; set; }
    public double RttAvg { get; set; }
    public long RttMin { get; set; }
    public long RttMax { get; set; }
}

/// <summary>One model-warning instance (an admin note): opened when its
/// condition starts holding, closed when it stops. Ended rows are pruned with
/// transitions (Retention.TransitionMonths).</summary>
public class ModelWarningRow
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string? TargetName { get; set; }
    public string Message { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
}
