using System.Text.Json;
using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Settings;
using ConPersistence.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class SnapshotImporterTests
{
    private static string FixturePath(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ConnectionWatcher.sln")))
                return Path.Combine(dir.FullName, "Tests", "contracts", name);
        }
        throw new InvalidOperationException($"ConnectionWatcher.sln not found above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public async Task Import_raises_ConfigChanged_once_on_the_service_it_is_given()
    {
        using var factory = new TestDbFactory();
        var store = new ConfigStore(factory);
        var service = new ConfigService(factory, store);
        var changes = 0;
        service.ConfigChanged += () => changes++;

        await SnapshotImporter.ImportAsync(factory, service, new Snapshot
        {
            Config = new ConfigDocument
            {
                Profiles = [new ProfileDto { Name = "Internal" }],
                Targets = [new TargetDto { Name = "Router", Address = "192.168.1.1", Profile = "Internal" }],
            },
        });

        Assert.Equal(1, changes);
        Assert.Single((await store.LoadWatcherOptionsAsync()).Targets);
    }

    /// <summary>A database holding one target, one transition, one setting: what a refused import must leave alone.</summary>
    private static async Task<(TestDbFactory Factory, ConfigService Service, ConfigStore Store)> SeededAsync()
    {
        var factory = new TestDbFactory();
        var store = new ConfigStore(factory);
        var service = new ConfigService(factory, store);
        await SnapshotImporter.ImportAsync(factory, service, new Snapshot
        {
            Config = new ConfigDocument
            {
                Profiles = [new ProfileDto { Name = "Internal" }],
                Targets = [new TargetDto { Name = "Router", Address = "192.0.2.1", Profile = "Internal" }],
            },
            Settings = new() { ["Verdict.AnchorPool"] = "Kept" },
            Transitions = [new TransitionRow
            {
                Id = 7, TimestampUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                SubjectId = Guid.NewGuid(), SubjectName = "Router", ToState = "Responding",
            }],
        });
        return (factory, service, store);
    }

    private static async Task AssertUntouchedAsync(TestDbFactory factory, ConfigStore store)
    {
        await using var db = factory.CreateDbContext();
        Assert.Equal(7, (await db.Transitions.SingleAsync()).Id);
        Assert.Equal("Kept", (await db.Settings.SingleAsync(s => s.Key == "Verdict.AnchorPool")).Value);
        Assert.Equal("Router", Assert.Single((await store.LoadWatcherOptionsAsync()).Targets).Name);
    }

    [Fact]
    public async Task A_newer_schema_snapshot_is_refused_and_changes_nothing()
    {
        var (factory, service, store) = await SeededAsync();
        using var _ = factory;
        var changes = 0;
        service.ConfigChanged += () => changes++;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SnapshotImporter.ImportAsync(factory, service, new Snapshot
            {
                SchemaVersion = 2,
                Config = new ConfigDocument
                {
                    Profiles = [new ProfileDto { Name = "Internal" }],
                    Targets = [new TargetDto { Name = "Other", Address = "192.0.2.9", Profile = "Internal" }],
                },
                Transitions = [new TransitionRow { Id = 99, SubjectName = "Other" }],
            }));

        Assert.Contains("2", ex.Message);
        Assert.Equal(0, changes);
        await AssertUntouchedAsync(factory, store);
    }

    [Fact]
    public async Task An_invalid_config_snapshot_is_refused_before_any_delete()
    {
        var (factory, service, store) = await SeededAsync();
        using var _ = factory;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SnapshotImporter.ImportAsync(factory, service, new Snapshot
            {
                Config = new ConfigDocument
                {
                    Profiles = [new ProfileDto { Name = "Internal" }],
                    Targets = [new TargetDto { Name = "Broken", Address = "192.0.2.9", Profile = "NoSuchProfile" }],
                },
                Settings = new() { ["Verdict.AnchorPool"] = "Replaced" },
                Transitions = [new TransitionRow { Id = 99, SubjectName = "Broken" }],
            }));

        Assert.Contains("NoSuchProfile", ex.Message); // the validator's reason reaches the caller
        await AssertUntouchedAsync(factory, store);
    }

    private static Snapshot OtherSnapshot() => new()
    {
        Config = new ConfigDocument
        {
            Profiles = [new ProfileDto { Name = "Internal" }],
            Targets = [new TargetDto { Name = "Other", Address = "192.0.2.9", Profile = "Internal" }],
        },
        Transitions = [new TransitionRow { Id = 99, SubjectName = "Other" }],
    };

    /// <summary>Delegates, but throws on the Nth context request (1-based).</summary>
    private sealed class FailingOnNthDbFactory(IDbContextFactory<ConWatchDbContext> inner, int failOn)
        : IDbContextFactory<ConWatchDbContext>
    {
        private int _calls;

        public ConWatchDbContext CreateDbContext() => Next() ? throw new InvalidOperationException("simulated SQLITE_BUSY") : inner.CreateDbContext();

        public Task<ConWatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Next() ? throw new InvalidOperationException("simulated SQLITE_BUSY") : inner.CreateDbContextAsync(cancellationToken);

        private bool Next() => Interlocked.Increment(ref _calls) == failOn;
    }

    [Fact]
    public async Task A_database_failure_in_the_config_write_says_the_config_was_not_committed()
    {
        var (real, _, store) = await SeededAsync();
        using var _ = real;
        var factory = new FailingOnceDbFactory(real); // the first context request is the config write's
        var service = new ConfigService(factory, new ConfigStore(factory));

        var ex = await Assert.ThrowsAsync<SnapshotImportException>(() =>
            SnapshotImporter.ImportAsync(factory, service, OtherSnapshot()));

        Assert.False(ex.ConfigCommitted);
        Assert.Contains("simulated SQLITE_BUSY", ex.Message);
        await AssertUntouchedAsync(real, store);
    }

    [Fact]
    public async Task A_database_failure_in_the_data_phase_says_the_config_was_committed()
    {
        var (real, _, store) = await SeededAsync();
        using var _ = real;
        var factory = new FailingOnNthDbFactory(real, failOn: 2); // 1 = config write, 2 = data phase
        var service = new ConfigService(factory, new ConfigStore(factory));

        var ex = await Assert.ThrowsAsync<SnapshotImportException>(() =>
            SnapshotImporter.ImportAsync(factory, service, OtherSnapshot()));

        Assert.True(ex.ConfigCommitted);
        Assert.Equal("Other", Assert.Single((await store.LoadWatcherOptionsAsync()).Targets).Name);
    }

    [Fact]
    public async Task A_2026_09_snapshot_imports()
    {
        // The shape the first public snapshots had: no scope, critical, systemManaged or
        // description keys anywhere. Hand-built and frozen; it must keep importing.
        var snapshot = JsonSerializer.Deserialize<Snapshot>(
            await File.ReadAllTextAsync(FixturePath("snapshot-2026-09.json")), JsonSerializerOptions.Web)!;
        using var factory = new TestDbFactory();
        var store = new ConfigStore(factory);

        await SnapshotImporter.ImportAsync(factory, new ConfigService(factory, store), snapshot);

        var doc = await store.ExportAsync();
        Assert.Equal(3, doc.Targets.Count);
        Assert.Equal(["Alpha", "Bravo", "Modem"], doc.Targets.Select(t => t.Name).Order());
        Assert.All(doc.Targets, t =>
        {
            Assert.Equal(TargetScope.Verdict, t.Scope);
            Assert.False(t.Critical);
            Assert.False(t.SystemManaged);
            Assert.Equal("External", t.Profile);
        });
        Assert.Equal("Modem", doc.Targets.Single(t => t.Name == "Alpha").Parent);
        Assert.Equal(["Alpha", "Bravo"], doc.Pools.Single().Members);
        await using var db = factory.CreateDbContext();
        Assert.Equal([101L, 102, 103, 104, 105], await db.Transitions.OrderBy(t => t.Id).Select(t => t.Id).ToListAsync());
        Assert.All(await db.Transitions.ToListAsync(), t => Assert.Equal(DateTimeKind.Utc, t.TimestampUtc.Kind));
        Assert.Equal("Internet", (await db.Settings.SingleAsync(s => s.Key == SettingKeys.AnchorPool)).Value);
    }
}
