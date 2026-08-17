using ConPersistence;
using ConPersistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class DbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ConWatchDbContext> _options;

    public DbContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite(_connection).Options;
        using var db = new ConWatchDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Full_object_graph_roundtrips()
    {
        var profileId = Guid.NewGuid();
        var routerId = Guid.NewGuid();
        var googleId = Guid.NewGuid();
        var poolId = Guid.NewGuid();

        using (var db = new ConWatchDbContext(_options))
        {
            db.Profiles.Add(new ProfileRow { Id = profileId, Name = "Internal", NormalIntervalSeconds = 1 });
            db.Targets.Add(new TargetRow { Id = routerId, Name = "Router", Address = "192.168.1.1", ProfileId = profileId });
            db.Targets.Add(new TargetRow { Id = googleId, Name = "Google", Address = "8.8.8.8", ParentId = routerId, ProfileId = profileId });
            db.Pools.Add(new PoolRow
            {
                Id = poolId, Name = "Internet", ParentId = routerId,
                Members = [new PoolMemberRow { PoolId = poolId, TargetId = googleId, MemberIndex = 0 }],
            });
            db.Transitions.Add(new TransitionRow
            {
                TimestampUtc = DateTime.UtcNow, SubjectId = googleId, SubjectName = "Google",
                SubjectKind = "Target", FromState = "Responding", ToState = "Down",
                Trigger = "ThresholdCrossed", SuccessRate = 0.05, SampleSize = 20, Verdict = null,
            });
            db.Rollups.Add(new RollupRow
            {
                TargetId = googleId, PeriodStartUtc = DateTime.UtcNow.Date, Resolution = "Minute",
                PingCount = 60, SuccessCount = 59, RttAvg = 12.5, RttMin = 8, RttMax = 40,
            });
            db.SaveChanges();
        }

        using (var db = new ConWatchDbContext(_options))
        {
            var google = db.Targets.Single(t => t.Name == "Google");
            Assert.Equal(routerId, google.ParentId);
            var pool = db.Pools.Include(p => p.Members).Single();
            Assert.Single(pool.Members);
            Assert.Equal(googleId, pool.Members[0].TargetId);
            Assert.Equal(1, db.Transitions.Count());
            Assert.Equal(1, db.Rollups.Count());
        }
    }

    [Fact]
    public void Duplicate_target_name_is_rejected_by_unique_index()
    {
        using var db = new ConWatchDbContext(_options);
        var profileId = Guid.NewGuid();
        db.Profiles.Add(new ProfileRow { Id = profileId, Name = "P" });
        db.Targets.Add(new TargetRow { Id = Guid.NewGuid(), Name = "Router", Address = "10.0.0.1", ProfileId = profileId });
        db.Targets.Add(new TargetRow { Id = Guid.NewGuid(), Name = "Router", Address = "10.0.0.2", ProfileId = profileId });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void Duplicate_rollup_period_is_rejected_by_unique_index()
    {
        using var db = new ConWatchDbContext(_options);
        var targetId = Guid.NewGuid();
        var period = DateTime.UtcNow.Date;
        db.Rollups.Add(new RollupRow { TargetId = targetId, PeriodStartUtc = period, Resolution = "Minute", PingCount = 1 });
        db.Rollups.Add(new RollupRow { TargetId = targetId, PeriodStartUtc = period, Resolution = "Minute", PingCount = 2 });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
