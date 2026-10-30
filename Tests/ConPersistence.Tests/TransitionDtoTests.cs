using System.Text.Json;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using ConPersistence.Entities;
using ConPersistence.Queries;

namespace ConPersistence.Tests;

public class TransitionDtoTests
{
    private static readonly Guid Subject = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Ancestor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Live_transition_maps_every_field_and_normalizes_the_offset()
    {
        var t = new TargetTransition
        {
            Kind = TransitionSubjectKind.Target, TargetId = Subject, TargetName = "Modem",
            From = HostState.Responding, To = HostState.Unreachable,
            Timestamp = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(2)),
            Reason = new TransitionReason
            {
                Trigger = TransitionTrigger.ParentDown, AncestorId = Ancestor,
                SuccessRate = 0.5, SampleSize = 10, BurstLossRate = 0.25, Detail = "d", Verdict = null,
            },
            AverageRoundtripTime = 7.5f,
        };

        var dto = TransitionDto.From(t);

        Assert.Null(dto.Id);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero), dto.Timestamp);
        Assert.Equal(TimeSpan.Zero, dto.Timestamp.Offset);
        Assert.Equal(Subject, dto.SubjectId);
        Assert.Equal("Modem", dto.SubjectName);
        Assert.Equal("Target", dto.SubjectKind);
        Assert.Equal("Responding", dto.FromState);
        Assert.Equal("Unreachable", dto.ToState);
        Assert.Equal("ParentDown", dto.Trigger);
        Assert.Equal(0.5, dto.SuccessRate);
        Assert.Equal(10, dto.SampleSize);
        Assert.Equal(0.25, dto.BurstLossRate);
        Assert.Equal(Ancestor, dto.AncestorId);
        Assert.Equal("d", dto.Detail);
        Assert.Null(dto.Verdict);
        Assert.Equal(7.5f, dto.AvgRtt);
    }

    [Fact]
    public void Row_timestamp_without_a_kind_is_read_as_utc()
    {
        var row = new TransitionRow
        {
            Id = 5, SubjectId = Subject, SubjectName = "Router", SubjectKind = "Target",
            FromState = "Starting", ToState = "Responding", Trigger = "ThresholdCrossed",
            TimestampUtc = new DateTime(2026, 9, 18, 10, 0, 0, 123, DateTimeKind.Unspecified),
        };

        var dto = TransitionDto.From(row);

        Assert.Equal(5, dto.Id);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 10, 0, 0, 123, TimeSpan.Zero), dto.Timestamp);
    }

    [Fact]
    public void Live_json_has_from_and_to_and_no_id()
    {
        var json = JsonSerializer.SerializeToElement(TransitionDto.From(new TargetTransition
        {
            TargetId = Subject, TargetName = "Router", From = HostState.Starting, To = HostState.Responding,
            Timestamp = DateTimeOffset.Parse("2026-09-18T10:00:00.5Z"),
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed },
        }), JsonSerializerOptions.Web);

        Assert.False(json.TryGetProperty("id", out _));
        Assert.Equal("Starting", json.GetProperty("from").GetString());
        Assert.Equal("Responding", json.GetProperty("to").GetString());
        Assert.Equal("2026-09-18T10:00:00.5+00:00", json.GetProperty("timestamp").GetString());
    }

    [Fact]
    public void Row_whose_subject_is_a_resolver_target_reads_as_Resolver_scope()
    {
        var row = new TransitionRow
        {
            Id = 6, SubjectId = Subject, SubjectName = "1.1.1.1", SubjectKind = "Target",
            FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
            TimestampUtc = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc),
        };
        var none = new HashSet<Guid>();
        var subject = new HashSet<Guid> { Subject };

        Assert.Equal("Resolver", TransitionDto.From(row, new ScopeTags(none, subject, subject)).Scope);
        Assert.Equal("Local", TransitionDto.From(row, new ScopeTags(subject, none, subject)).Scope);
        Assert.Equal("Verdict", TransitionDto.From(row, new ScopeTags(none, none, subject)).Scope);
        Assert.Equal("Unknown", TransitionDto.From(row, new ScopeTags(none, none, none)).Scope);
        row.SubjectKind = "Pool";
        Assert.Equal("Verdict", TransitionDto.From(row, new ScopeTags(none, none, none)).Scope);
    }
}
