using System.Text.Json.Serialization;
using ConEngine.Events;
using ConPersistence.Entities;

namespace ConPersistence.Queries;

/// <summary>
/// The one wire shape of a state transition, whether read back from the log
/// (GET /api/v1/transitions, the MCP get_transitions tool) or pushed live
/// (SignalR "transition", webhooks, MQTT). Timestamp is a DateTimeOffset at
/// offset zero, which System.Text.Json writes as
/// "2026-09-18T10:00:00.123+00:00" — the encoding the REST endpoint and the
/// SignalR message already used.
/// </summary>
public sealed record TransitionDto
{
    /// <summary>The log row id (the paging cursor); absent on live messages.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid SubjectId { get; init; }
    public required string SubjectName { get; init; }
    public required string SubjectKind { get; init; }
    [JsonPropertyName("from")]
    public required string FromState { get; init; }
    [JsonPropertyName("to")]
    public required string ToState { get; init; }
    public required string Trigger { get; init; }
    public double? SuccessRate { get; init; }
    public int? SampleSize { get; init; }
    public double? BurstLossRate { get; init; }
    public Guid? AncestorId { get; init; }
    public string? Detail { get; init; }
    public string? Verdict { get; init; }
    public float AvgRtt { get; init; }

    public static TransitionDto From(TargetTransition t) => new()
    {
        Timestamp = t.Timestamp.ToUniversalTime(),
        SubjectId = t.TargetId,
        SubjectName = t.TargetName,
        SubjectKind = t.Kind.ToString(),
        FromState = t.From.ToString(),
        ToState = t.To.ToString(),
        Trigger = t.Reason.Trigger.ToString(),
        SuccessRate = t.Reason.SuccessRate,
        SampleSize = t.Reason.SampleSize,
        BurstLossRate = t.Reason.BurstLossRate,
        AncestorId = t.Reason.AncestorId,
        Detail = t.Reason.Detail,
        Verdict = t.Reason.Verdict,
        AvgRtt = t.AverageRoundtripTime,
    };

    public static TransitionDto From(TransitionRow r) => new()
    {
        Id = r.Id,
        // Tagged explicitly: every *Utc column is UTC by rule, and a value that
        // reaches here as Unspecified must not pick up the server's offset.
        Timestamp = new DateTimeOffset(DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc)),
        SubjectId = r.SubjectId,
        SubjectName = r.SubjectName,
        SubjectKind = r.SubjectKind,
        FromState = r.FromState,
        ToState = r.ToState,
        Trigger = r.Trigger,
        SuccessRate = r.SuccessRate,
        SampleSize = r.SampleSize,
        BurstLossRate = r.BurstLossRate,
        AncestorId = r.AncestorId,
        Detail = r.Detail,
        Verdict = r.Verdict,
        AvgRtt = r.AvgRtt,
    };
}
