using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Snapshots;
using ConWatch.Clone;

namespace ConWatch.Tests;

/// <summary>Spec 2026-10-10 §5.1: a clone never carries the source's system
/// resolvers — the destination regenerates them from its own OS.</summary>
public class CloneWithoutSystemManagedTests
{
    private static readonly Guid ManagedId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid UpstreamId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly DateTime Stamp = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static Snapshot Build() => new()
    {
        Config = new ConfigDocument
        {
            Targets =
            {
                new TargetDto
                {
                    Id = ManagedId, Name = "Your resolver 10.1.10.1", Address = "10.1.10.1", Profile = "Resolver",
                    Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true,
                },
                new TargetDto
                {
                    Id = UpstreamId, Name = "1.1.1.1 directly", Address = "1.1.1.1", Profile = "Resolver",
                    Probe = "dns", Scope = TargetScope.Resolver,
                },
            },
        },
        Transitions =
        {
            new TransitionRow { TimestampUtc = Stamp, SubjectId = ManagedId, SubjectName = "Your resolver 10.1.10.1" },
            new TransitionRow { TimestampUtc = Stamp, SubjectId = UpstreamId, SubjectName = "1.1.1.1 directly" },
        },
        Rollups =
        {
            new RollupRow { TargetId = ManagedId, PeriodStartUtc = Stamp },
            new RollupRow { TargetId = UpstreamId, PeriodStartUtc = Stamp },
        },
        Glitches =
        {
            new GlitchRow { TargetId = ManagedId, TargetName = "Your resolver 10.1.10.1", StartUtc = Stamp, EndUtc = Stamp },
        },
    };

    [Fact]
    public void System_managed_targets_and_their_rows_are_dropped_upstream_resolvers_stay()
    {
        var before = Build();
        var after = CloneService.WithoutSystemManaged(before);

        Assert.Equal([UpstreamId], after.Config.Targets.Select(t => t.Id!.Value));
        Assert.Equal([UpstreamId], after.Transitions.Select(t => t.SubjectId));
        Assert.Equal([UpstreamId], after.Rollups.Select(r => r.TargetId));
        Assert.Empty(after.Glitches);

        Assert.Equal(2, before.Config.Targets.Count); // input not mutated
    }
}
