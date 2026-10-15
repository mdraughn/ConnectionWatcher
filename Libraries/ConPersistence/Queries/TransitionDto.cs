using System.Text.Json.Serialization;
using ConCore.Targets;
using ConEngine.Events;
using ConPersistence.Entities;

namespace ConPersistence.Queries;

/// <summary>The DnsDown blame on a verdict transition, as webhooks and MQTT publish it.</summary>
public sealed record TransitionDnsBlame(string Kind, string Message, IReadOnlyList<string> Resolvers)
{
    public static TransitionDnsBlame? From(ConEngine.Topology.DnsBlame? b) =>
        b is null ? null : new(b.Kind.ToString(), b.Message, b.Resolvers);
}

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
    /// <summary>"Verdict", "Local" or "Resolver" on history rows (REST /transitions, MCP get_transitions);
    /// omitted on live messages, whose subscribers know their targets.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetScope? Scope { get; init; }
    /// <summary>Present only on a DnsDown verdict transition (live messages); history rows do not keep it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransitionDnsBlame? Dns { get; init; }

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
        Dns = TransitionDnsBlame.From(t.Reason.Dns),
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

    /// <summary>A history row tagged with its subject's current scope; pools, the verdict
    /// and since-deleted targets read as <see cref="TargetScope.Verdict"/>.</summary>
    public static TransitionDto From(TransitionRow r, ISet<Guid> localIds) =>
        From(r, localIds, new HashSet<Guid>());

    /// <summary>As above, also tagging <see cref="TargetScope.Resolver"/> subjects.</summary>
    public static TransitionDto From(TransitionRow r, ISet<Guid> localIds, ISet<Guid> resolverIds) => From(r) with
    {
        Scope = localIds.Contains(r.SubjectId) ? TargetScope.Local
            : resolverIds.Contains(r.SubjectId) ? TargetScope.Resolver
            : TargetScope.Verdict,
    };
}
