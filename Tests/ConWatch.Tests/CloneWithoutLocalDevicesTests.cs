using ConCore.Targets;
using ConEngine.Topology;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Snapshots;
using ConWatch.Clone;

namespace ConWatch.Tests;

/// <summary>Direct tests of the clone filter: Local targets and every row recorded for
/// them disappear, nothing else does, and the input snapshot is not mutated.</summary>
public class CloneWithoutLocalDevicesTests
{
    private static readonly Guid LocalId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid VerdictId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTime Stamp = new(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Snapshot Build() => new()
    {
        SchemaVersion = 1,
        Config = new ConfigDocument
        {
            Targets =
            {
                new TargetDto { Id = LocalId, Name = "NasBox", Address = "203.0.113.9", Scope = TargetScope.Local },
                new TargetDto { Id = VerdictId, Name = "Router", Address = "198.51.100.1", Scope = TargetScope.Verdict },
            },
            Pools = { new PoolDto { Name = "Anchors", Members = { "Router" } } },
        },
        Transitions =
        {
            new TransitionRow { TimestampUtc = Stamp, SubjectId = LocalId, SubjectName = "NasBox" },
            new TransitionRow { TimestampUtc = Stamp, SubjectId = VerdictId, SubjectName = "Router" },
            new TransitionRow
            {
                TimestampUtc = Stamp, SubjectId = VerdictCoordinator.SubjectId,
                SubjectName = "Internet", SubjectKind = "Verdict",
            },
        },
        Rollups =
        {
            new RollupRow { TargetId = LocalId, PeriodStartUtc = Stamp },
            new RollupRow { TargetId = VerdictId, PeriodStartUtc = Stamp },
        },
        Glitches =
        {
            new GlitchRow { TargetId = LocalId, TargetName = "NasBox", StartUtc = Stamp, EndUtc = Stamp },
            new GlitchRow { TargetId = VerdictId, TargetName = "Router", StartUtc = Stamp, EndUtc = Stamp },
        },
    };

    [Fact]
    public void Removes_Local_targets_and_all_their_rows_but_nothing_else()
    {
        var result = CloneService.WithoutLocalDevices(Build());

        var target = Assert.Single(result.Config.Targets);
        Assert.Equal(VerdictId, target.Id);
        Assert.Single(result.Config.Pools);

        Assert.Equal(2, result.Transitions.Count);
        Assert.Contains(result.Transitions, t => t.SubjectId == VerdictId);
        Assert.Contains(result.Transitions, t => t.SubjectId == VerdictCoordinator.SubjectId);
        Assert.Equal(VerdictId, Assert.Single(result.Rollups).TargetId);
        Assert.Equal(VerdictId, Assert.Single(result.Glitches).TargetId);

        Assert.DoesNotContain(result.Transitions, t => t.SubjectId == LocalId);
        Assert.DoesNotContain(result.Rollups, r => r.TargetId == LocalId);
        Assert.DoesNotContain(result.Glitches, g => g.TargetId == LocalId);
    }

    [Fact]
    public void Does_not_mutate_the_input_snapshot()
    {
        var input = Build();
        CloneService.WithoutLocalDevices(input);

        Assert.Equal(2, input.Config.Targets.Count);
        Assert.Equal(3, input.Transitions.Count);
        Assert.Equal(2, input.Rollups.Count);
        Assert.Equal(2, input.Glitches.Count);
    }

    [Fact]
    public void A_snapshot_without_Local_targets_comes_back_with_the_same_content()
    {
        var input = Build() with
        {
            Config = new ConfigDocument
            {
                Targets = { new TargetDto { Id = VerdictId, Name = "Router", Address = "198.51.100.1" } },
                Pools = { new PoolDto { Name = "Anchors", Members = { "Router" } } },
            },
        };
        input.Transitions.RemoveAll(t => t.SubjectId == LocalId);
        input.Rollups.RemoveAll(r => r.TargetId == LocalId);
        input.Glitches.RemoveAll(g => g.TargetId == LocalId);

        var result = CloneService.WithoutLocalDevices(input);

        Assert.Single(result.Config.Targets);
        Assert.Single(result.Config.Pools);
        Assert.Equal(2, result.Transitions.Count);
        Assert.Single(result.Rollups);
        Assert.Single(result.Glitches);
    }
}
