using ConPersistence.Entities;
using ConPersistence.Services;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class DataPrunerTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly TestDbFactory _factory = new();
    private readonly Guid _target = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Prunes_before_the_date_keeps_after_and_each_subjects_latest()
    {
        var quiet = Guid.NewGuid(); // last transition long before the cutoff
        await using (var db = _factory.CreateDbContext())
        {
            db.Transitions.AddRange(
                Row(_target, Cutoff.AddMonths(-6)),
                Row(_target, Cutoff.AddDays(-1)),
                Row(_target, Cutoff.AddDays(2)),
                Row(quiet, Cutoff.AddYears(-1)),
                Row(quiet, Cutoff.AddMonths(-10)));
            db.Rollups.AddRange(
                Rollup(Cutoff.AddDays(-3)), Rollup(Cutoff.AddDays(-2)), Rollup(Cutoff.AddDays(5)));
            db.Glitches.AddRange(Glitch(Cutoff.AddDays(-4)), Glitch(Cutoff.AddDays(4)));
            await db.SaveChangesAsync();
        }

        PruneResult result;
        await using (var db = _factory.CreateDbContext())
            // In-memory database: the file-size probe just reports 0/0.
            result = await DataPruner.PruneBeforeAsync(db, "no-such-file.db", Cutoff);

        Assert.Equal(3, result.Transitions); // both old _target rows + quiet's older row
        Assert.Equal(2, result.Rollups);
        Assert.Equal(1, result.Glitches);

        await using var check = _factory.CreateDbContext();
        var transitions = await check.Transitions.ToListAsync();
        Assert.Equal(2, transitions.Count);
        Assert.Contains(transitions, t => t.SubjectId == _target && t.TimestampUtc == Cutoff.AddDays(2));
        // The quiet subject's LATEST row survives even though it predates the
        // cutoff: warm restart seeds the presumed state from it.
        Assert.Contains(transitions, t => t.SubjectId == quiet && t.TimestampUtc == Cutoff.AddMonths(-10));
        Assert.Equal(1, await check.Rollups.CountAsync());
        Assert.Equal(1, await check.Glitches.CountAsync());
    }

    private TransitionRow Row(Guid subject, DateTime at) => new()
    {
        TimestampUtc = at, SubjectId = subject, SubjectName = "T", SubjectKind = "Target",
        FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
    };

    private RollupRow Rollup(DateTime at) => new()
    {
        TargetId = _target, PeriodStartUtc = at, Resolution = "Minute",
        PingCount = 6, SuccessCount = 6, RttAvg = 10, RttMin = 8, RttMax = 12,
    };

    private GlitchRow Glitch(DateTime at) => new()
    {
        TargetId = _target, TargetName = "T", Class = 0,
        StartUtc = at, EndUtc = at.AddSeconds(10), LostCount = 2, SampleCount = 8,
    };
}
