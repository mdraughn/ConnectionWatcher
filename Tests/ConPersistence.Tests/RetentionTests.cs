using ConPersistence.Entities;
using ConPersistence.Rollups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConPersistence.Tests;

public class RetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDbFactory _factory = new();
    private readonly PersistenceOptions _options = new() { MinuteRetentionDays = 90, HourlyRetentionMonths = 13 };
    private readonly RetentionPass _pass = new(NullLogger<RetentionPass>.Instance);
    private readonly Guid _target = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    private async Task AddMinuteRows(DateTime hourStart, int count, int pingsEach, int successEach, double avg, long min, long max)
    {
        await using var db = _factory.CreateDbContext();
        for (int i = 0; i < count; i++)
        {
            db.Rollups.Add(new RollupRow
            {
                TargetId = _target, PeriodStartUtc = hourStart.AddMinutes(i), Resolution = "Minute",
                PingCount = pingsEach, SuccessCount = successEach, RttAvg = avg, RttMin = min, RttMax = max,
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Old_minutes_become_one_hour_row_with_weighted_math()
    {
        var oldHour = Now.AddDays(-91).Date; // well past the 90-day boundary
        // 30 minutes at avg 10 (5 successes each), 30 minutes at avg 30 (5 successes each)
        await AddMinuteRows(oldHour, 30, 6, 5, 10.0, 8, 15);
        await AddMinuteRows(oldHour.AddMinutes(30), 30, 6, 5, 30.0, 20, 45);
        // Recent minutes must survive.
        await AddMinuteRows(Now.AddHours(-2), 10, 6, 5, 12.0, 10, 14);

        await using (var db = _factory.CreateDbContext())
        {
            var converted = await _pass.RunAsync(db, Now, _options, CancellationToken.None);
            Assert.True(converted > 0);
        }

        await using (var check = _factory.CreateDbContext())
        {
            var hourRow = Assert.Single(await check.Rollups
                .Where(r => r.Resolution == "Hour").ToListAsync());
            Assert.Equal(oldHour, hourRow.PeriodStartUtc);
            Assert.Equal(360, hourRow.PingCount);       // 60 * 6
            Assert.Equal(300, hourRow.SuccessCount);    // 60 * 5
            Assert.Equal(20.0, hourRow.RttAvg, 3);      // equal weights: (10+30)/2
            Assert.Equal(8, hourRow.RttMin);
            Assert.Equal(45, hourRow.RttMax);

            // Old minutes deleted, recent ones intact.
            Assert.Equal(10, await check.Rollups.CountAsync(r => r.Resolution == "Minute"));
        }
    }

    [Fact]
    public async Task Retention_pass_is_idempotent()
    {
        await AddMinuteRows(Now.AddDays(-100).Date, 60, 6, 5, 10.0, 8, 15);

        await using (var db = _factory.CreateDbContext())
            await _pass.RunAsync(db, Now, _options, CancellationToken.None);
        await using (var db = _factory.CreateDbContext())
        {
            var second = await _pass.RunAsync(db, Now, _options, CancellationToken.None);
            Assert.Equal(0, second);
        }

        await using var checkDb = _factory.CreateDbContext();
        var hourRow = Assert.Single(await checkDb.Rollups.Where(r => r.Resolution == "Hour").ToListAsync());
        Assert.Equal(360, hourRow.PingCount);
    }

    [Fact]
    public async Task Very_old_hours_become_day_rows()
    {
        var oldDay = Now.AddMonths(-14).Date;
        await using (var db = _factory.CreateDbContext())
        {
            for (int h = 0; h < 24; h++)
            {
                db.Rollups.Add(new RollupRow
                {
                    TargetId = _target, PeriodStartUtc = oldDay.AddHours(h), Resolution = "Hour",
                    PingCount = 360, SuccessCount = 300, RttAvg = 20, RttMin = 8, RttMax = 45,
                });
            }
            await db.SaveChangesAsync();
            await _pass.RunAsync(db, Now, _options, CancellationToken.None);
        }

        await using var check = _factory.CreateDbContext();
        var dayRow = Assert.Single(await check.Rollups.Where(r => r.Resolution == "Day").ToListAsync());
        Assert.Equal(oldDay, dayRow.PeriodStartUtc);
        Assert.Equal(24 * 360, dayRow.PingCount);
        Assert.Equal(20.0, dayRow.RttAvg, 3);
        Assert.Equal(0, await check.Rollups.CountAsync(r => r.Resolution == "Hour"));
    }

    [Fact]
    public async Task Transitions_are_never_touched()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Transitions.Add(new TransitionRow
            {
                TimestampUtc = Now.AddYears(-3), SubjectId = _target, SubjectName = "Old",
                SubjectKind = "Target", FromState = "Responding", ToState = "Down",
                Trigger = "ThresholdCrossed",
            });
            await db.SaveChangesAsync();
            await _pass.RunAsync(db, Now, _options, CancellationToken.None);
        }
        await using var check = _factory.CreateDbContext();
        Assert.Equal(1, await check.Transitions.CountAsync());
    }
}
