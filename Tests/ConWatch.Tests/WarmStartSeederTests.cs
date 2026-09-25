using ConEngine.Topology;
using ConEngine.Tracking;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatch.Tests;

public sealed class WarmStartSeederTests : IAsyncLifetime
{
    private sealed class FileDb(string path) : IDbContextFactory<ConWatchDbContext>
    {
        private readonly DbContextOptions<ConWatchDbContext> _options =
            new DbContextOptionsBuilder<ConWatchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;

        public ConWatchDbContext CreateDbContext() => new(_options);
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("seeder-test").FullName;
    private FileDb _db = null!;
    private ConfigService _config = null!;
    private SettingsStore _settings = null!;
    private WarmStartSeeder _seeder = null!;

    public async Task InitializeAsync()
    {
        _db = new FileDb(Path.Combine(_dir, "seed.db"));
        await DatabaseStartup.PrepareAsync(_db, CancellationToken.None);
        var store = new ConfigStore(_db);
        await store.SeedAsync(null); // built-in profiles
        _config = new ConfigService(_db, store);
        _settings = new SettingsStore(_db);
        _seeder = new WarmStartSeeder(_db, store, _settings, TimeProvider.System,
            NullLogger<WarmStartSeeder>.Instance);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<Guid> AddTarget(string name, string address, string? parent = null) =>
        (await _config.UpsertTargetAsync(new TargetDto
        {
            Name = name, Address = address, Parent = parent, Profile = "Internal",
        })).Id!.Value;

    private async Task Record(Guid subject, string kind, string to, DateTime atUtc,
        string trigger = "ThresholdCrossed", string? verdict = null)
    {
        await using var db = _db.CreateDbContext();
        db.Transitions.Add(new TransitionRow
        {
            TimestampUtc = atUtc, SubjectId = subject, SubjectName = "x", SubjectKind = kind,
            FromState = "Starting", ToState = to, Trigger = trigger, Verdict = verdict,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Recent_states_seed_but_starting_rows_and_history_resets_do_not()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var modem = await AddTarget("Modem", "192.168.100.1", "Router");
        var tv = await AddTarget("TV", "192.168.1.50", "Router");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddMinutes(-2));
        await Record(modem, "Target", "Starting", now.AddMinutes(-2));
        await Record(tv, "Target", "Down", now.AddMinutes(-3));
        await Record(tv, "Target", "Starting", now.AddMinutes(-1), trigger: "HistoryReset");

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(HostState.Responding, seed.Targets[router].State);
        Assert.Equal("192.168.1.1", seed.Targets[router].Address);
        Assert.False(seed.Targets.ContainsKey(modem)); // Starting never seeds
        Assert.False(seed.Targets.ContainsKey(tv));    // its latest row is a HistoryReset
    }

    [Fact]
    public async Task A_log_older_than_the_gap_cold_starts()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        await Record(router, "Target", "Responding", DateTime.UtcNow.AddMinutes(-(SettingsDefaults.WarmRestartMaxGapMinutes + 5)));

        Assert.Null(await _seeder.FromDbAsync());
    }

    [Fact]
    public async Task Unreachable_without_a_down_ancestor_is_dropped()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var modem = await AddTarget("Modem", "192.168.100.1", "Router");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddMinutes(-2));
        await Record(modem, "Target", "Unreachable", now.AddMinutes(-2));

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.True(seed.Targets.ContainsKey(router));
        Assert.False(seed.Targets.ContainsKey(modem));
    }

    [Fact]
    public async Task Unreachable_behind_a_down_ancestor_is_kept()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var modem = await AddTarget("Modem", "192.168.100.1", "Router");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Down", now.AddMinutes(-2));
        await Record(modem, "Target", "Unreachable", now.AddMinutes(-2));

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(HostState.Down, seed.Targets[router].State);
        Assert.Equal(HostState.Unreachable, seed.Targets[modem].State);
    }

    [Fact]
    public async Task The_latest_verdict_row_seeds_the_verdict()
    {
        await AddTarget("Router", "192.168.1.1");
        await Record(VerdictCoordinator.SubjectId, "Verdict", "Responding", DateTime.UtcNow.AddMinutes(-1),
            trigger: "VerdictChanged", verdict: "Healthy");

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(VerdictState.Healthy, seed.Verdict);
    }

    [Fact]
    public async Task A_zero_gap_disables_all_seeding()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        await Record(router, "Target", "Responding", DateTime.UtcNow.AddMinutes(-1));
        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.WarmRestartMaxGapMinutes] = "0" });

        Assert.False(await _seeder.EnabledAsync());
        Assert.Null(await _seeder.FromDbAsync());
    }
}
