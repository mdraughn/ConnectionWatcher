using ConPersistence.Entities;

namespace ConPersistence.Tests;

public class HistoryQueryTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDbFactory _factory = new();
    private readonly PersistenceOptions _options = new() { MinuteRetentionDays = 90, HourlyRetentionMonths = 13 };

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("minute", -1, "Minute")]     // explicit always honored
    [InlineData("auto", -89, "Minute")]      // within minute retention
    [InlineData("auto", -91, "Hour")]        // past minute retention
    [InlineData("auto", -420, "Day")]        // ~14 months: past hourly retention
    public void ResolveResolution_picks_finest_stored(string requested, int fromDaysOffset, string expected)
    {
        var result = HistoryQueries.ResolveResolution(requested, Now.AddDays(fromDaysOffset), Now, _options);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Transitions_keyset_pages_are_disjoint_and_ordered()
    {
        var subject = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            for (int i = 0; i < 25; i++)
            {
                db.Transitions.Add(new TransitionRow
                {
                    TimestampUtc = Now.AddMinutes(i), SubjectId = subject, SubjectName = "S",
                    SubjectKind = "Target", FromState = "A", ToState = "B", Trigger = "ThresholdCrossed",
                });
            }
            await db.SaveChangesAsync();
        }

        await using var query = _factory.CreateDbContext();
        var page1 = await HistoryQueries.TransitionsAsync(query, subject, null, null, 10, null);
        Assert.Equal(10, page1.Count);
        Assert.True(page1.First().Id > page1.Last().Id); // newest first

        var page2 = await HistoryQueries.TransitionsAsync(query, subject, null, null, 10, page1.Last().Id);
        Assert.Equal(10, page2.Count);
        Assert.Empty(page1.Select(r => r.Id).Intersect(page2.Select(r => r.Id)));
        Assert.True(page2.First().Id < page1.Last().Id);

        var page3 = await HistoryQueries.TransitionsAsync(query, subject, null, null, 10, page2.Last().Id);
        Assert.Equal(5, page3.Count);
    }

    [Fact]
    public async Task Transitions_time_range_filters()
    {
        var subject = Guid.NewGuid();
        await using (var db = _factory.CreateDbContext())
        {
            for (int i = 0; i < 10; i++)
            {
                db.Transitions.Add(new TransitionRow
                {
                    TimestampUtc = Now.AddHours(i), SubjectId = subject, SubjectName = "S",
                    SubjectKind = "Target", FromState = "A", ToState = "B", Trigger = "ThresholdCrossed",
                });
            }
            await db.SaveChangesAsync();
        }

        await using var query = _factory.CreateDbContext();
        var rows = await HistoryQueries.TransitionsAsync(query, subject,
            Now.AddHours(2), Now.AddHours(5), 100, null);
        Assert.Equal(4, rows.Count); // hours 2,3,4,5 inclusive
    }
}
