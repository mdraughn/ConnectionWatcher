using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ConPersistence.Tests;

/// <summary>
/// An install from the very first schema, holding data, upgraded through the whole
/// migration chain. The in-memory TestDbFactory uses EnsureCreated and cannot show
/// what a migration does to rows that already exist.
/// </summary>
public class MigrationUpgradeTests : IDisposable
{
    private const string FirstMigration = "20260817145251_InitialCreate";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"conwatch-upgrade-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<ConWatchDbContext> _options;

    public MigrationUpgradeTests() =>
        _options = new DbContextOptionsBuilder<ConWatchDbContext>().UseSqlite($"Data Source={_path}").Options;

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_path}"));
        File.Delete(_path);
    }

    private sealed class Factory(DbContextOptions<ConWatchDbContext> options) : IDbContextFactory<ConWatchDbContext>
    {
        public ConWatchDbContext CreateDbContext() => new(options);
    }

    // SQL is built from test-generated GUIDs and literals, not user input.
#pragma warning disable EF1003
    [Fact]
    public async Task Upgrading_from_InitialCreate_keeps_data_and_defaults()
    {
        var profileId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var transitionAt = new DateTime(2026, 8, 18, 10, 0, 0, DateTimeKind.Utc);
        var rollupAt = new DateTime(2026, 8, 18, 10, 30, 0, DateTimeKind.Utc);

        await using (var db = new ConWatchDbContext(_options))
        {
            await db.GetService<IMigrator>().MigrateAsync(FirstMigration);
            // Text timestamps exactly as the first release wrote them.
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Profiles (Id, Name, NormalIntervalSeconds, PingTimeoutSeconds, BurstIntervalSeconds, " +
                "BurstMaxDurationSeconds, HistorySize, MinUsableSample, RespondingThreshold, FalteringThreshold, " +
                "DownThreshold, RecoveringThreshold) " +
                $"VALUES ('{profileId.ToString().ToUpperInvariant()}', 'External', 10, 3, 1, 60, 100, 10, 0.95, 0.75, 0.10, 0.25);");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Targets (Id, Name, Address, ProfileId, Enabled) " +
                $"VALUES ('{targetId.ToString().ToUpperInvariant()}', 'Google', '198.51.100.8', '{profileId.ToString().ToUpperInvariant()}', 1);");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Transitions (TimestampUtc, SubjectId, SubjectName, SubjectKind, FromState, ToState, " +
                "Trigger, SuccessRate, SampleSize, AvgRtt) " +
                $"VALUES ('2026-08-18 10:00:00', '{targetId.ToString().ToUpperInvariant()}', 'Google', 'Target', " +
                "'Starting', 'Responding', 'ThresholdCrossed', 1.0, 10, 12.5);");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Rollups (TargetId, PeriodStartUtc, Resolution, PingCount, SuccessCount, RttAvg, RttMin, RttMax) " +
                $"VALUES ('{targetId.ToString().ToUpperInvariant()}', '2026-08-18 10:30:00', 'Minute', 6, 6, 12.5, 10, 15);");
        }

        await using (var db = new ConWatchDbContext(_options))
            await db.Database.MigrateAsync();

        var doc = await new ConfigStore(new Factory(_options)).ExportAsync();
        var target = Assert.Single(doc.Targets);
        Assert.Equal(targetId, target.Id);
        Assert.Equal("Google", target.Name);
        Assert.Equal("198.51.100.8", target.Address);
        Assert.Equal(TargetScope.Verdict, target.Scope);   // columns added later take their defaults
        Assert.False(target.Critical);
        Assert.False(target.SystemManaged);
        Assert.Equal("Public", target.Profile);            // External was renamed on the way
        Assert.Equal("Public", Assert.Single(doc.Profiles).Name);

        await using var read = new ConWatchDbContext(_options);
        var transition = Assert.Single(await HistoryQueries.TransitionsAsync(read, null, null, null, 10, null));
        Assert.Equal(transitionAt, transition.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, transition.TimestampUtc.Kind);
        Assert.Equal("Responding", transition.ToState);
        Assert.Equal(12.5f, transition.AvgRtt);
        var rollup = Assert.Single(await HistoryQueries.RollupsAsync(read, targetId,
            rollupAt.AddHours(-1), rollupAt.AddHours(1), RollupResolution.Minute));
        Assert.Equal(rollupAt, rollup.PeriodStartUtc);
        Assert.Equal(DateTimeKind.Utc, rollup.PeriodStartUtc.Kind);
        // Alive until the minute rollup ended, later than the last transition.
        var alive = await WarmStartQueries.LastAliveUtcAsync(read);
        Assert.Equal(rollupAt.AddMinutes(1), alive);
        Assert.Equal(DateTimeKind.Utc, alive!.Value.Kind);
    }
#pragma warning restore EF1003
}
