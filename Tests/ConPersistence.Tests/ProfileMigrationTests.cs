using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ConPersistence.Config;

namespace ConPersistence.Tests;

/// <summary>
/// Runs the real migration chain against a file database frozen at the
/// migration before AddProfileDescription, seeded the way a 2026-09 install
/// looked, then migrates forward. The in-memory TestDbFactory uses
/// EnsureCreated and cannot exercise this.
/// </summary>
public class ProfileMigrationTests : IDisposable
{
    private const string BeforeDescription = "20260915162855_AddTargetProbe";
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"conwatch-mig-{Guid.NewGuid():N}.db");
    private readonly DbContextOptions<ConWatchDbContext> _options;

    public ProfileMigrationTests() =>
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

    private static string ProfileInsert(string name, double interval) =>
        $"INSERT INTO Profiles (Id, Name, NormalIntervalSeconds, PingTimeoutSeconds, BurstIntervalSeconds, " +
        $"BurstMaxDurationSeconds, HistorySize, MinUsableSample, RespondingThreshold, FalteringThreshold, " +
        $"DownThreshold, RecoveringThreshold) VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', " +
        $"'{name}', {interval}, 3, 1, 60, 100, 10, 0.95, 0.75, 0.10, 0.25);";

    private async Task SeedOldInstallAsync(params (string Name, double Interval)[] profiles)
    {
        await using var db = new ConWatchDbContext(_options);
        await db.GetService<IMigrator>().MigrateAsync(BeforeDescription);
        foreach (var (name, interval) in profiles)
            await db.Database.ExecuteSqlRawAsync(ProfileInsert(name, interval));
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO Targets (Id, Name, Address, ProfileId, Enabled) " +
            "SELECT '" + Guid.NewGuid().ToString().ToUpperInvariant() + "', 'Google', '8.8.8.8', Id, 1 " +
            "FROM Profiles WHERE Name = 'External';");
    }

    [Fact]
    public async Task External_becomes_Public_and_Path_is_added()
    {
        await SeedOldInstallAsync(("Internal", 1), ("External", 10));

        await using (var db = new ConWatchDbContext(_options))
            await db.Database.MigrateAsync();
        var store = new ConfigStore(new Factory(_options));
        await store.EnsureBuiltInsAsync();

        var doc = await store.ExportAsync();
        Assert.Equal(["Internal", "Path", "Public"], doc.Profiles.Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("Public", doc.Targets.Single(t => t.Name == "Google").Profile);
        Assert.Equal(10, doc.Profiles.Single(p => p.Name == "Public").NormalIntervalSeconds);
        Assert.All(doc.Profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public async Task Existing_custom_Public_leaves_External_alone()
    {
        await SeedOldInstallAsync(("Internal", 1), ("External", 10), ("Public", 20));

        await using (var db = new ConWatchDbContext(_options))
            await db.Database.MigrateAsync();
        var store = new ConfigStore(new Factory(_options));
        await store.EnsureBuiltInsAsync();

        var doc = await store.ExportAsync();
        Assert.Equal(["External", "Internal", "Path", "Public"], doc.Profiles.Select(p => p.Name).OrderBy(n => n));
        Assert.Equal("External", doc.Targets.Single(t => t.Name == "Google").Profile);
        Assert.Equal(20, doc.Profiles.Single(p => p.Name == "Public").NormalIntervalSeconds);
    }
}
