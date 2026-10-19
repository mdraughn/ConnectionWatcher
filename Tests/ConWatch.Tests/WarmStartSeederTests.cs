using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
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

    [Fact]
    public async Task A_pool_row_seeds_the_pool()
    {
        await AddTarget("Router", "192.168.1.1");
        var pool = Guid.NewGuid();
        await Record(pool, "Pool", "Responding", DateTime.UtcNow.AddMinutes(-1));

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(HostState.Responding, seed.Pools[pool]);
    }

    [Fact]
    public async Task An_Unknown_verdict_row_does_not_seed()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddMinutes(-2));
        await Record(VerdictCoordinator.SubjectId, "Verdict", "Responding", now.AddMinutes(-1),
            trigger: "VerdictChanged", verdict: "Unknown");

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Null(seed.Verdict);
        Assert.True(seed.Targets.ContainsKey(router));
    }

    [Fact]
    public async Task A_deleted_targets_latest_row_is_ignored()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var gone = await AddTarget("Gone", "192.168.1.77");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddMinutes(-2));
        await Record(gone, "Target", "Responding", now.AddMinutes(-2));
        Assert.True((await _config.DeleteTargetAsync(gone)).Success);

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.True(seed.Targets.ContainsKey(router));
        Assert.False(seed.Targets.ContainsKey(gone));
    }

    [Fact]
    public async Task Unreachable_grandchild_is_kept_when_the_Down_ancestor_is_two_levels_up()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var modem = await AddTarget("Modem", "192.168.100.1", "Router");
        var phone = await AddTarget("Phone", "192.168.100.9", "Modem");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Down", now.AddMinutes(-2));
        await Record(modem, "Target", "Unreachable", now.AddMinutes(-2));
        await Record(phone, "Target", "Unreachable", now.AddMinutes(-2));

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(HostState.Unreachable, seed.Targets[phone].State);
        Assert.Equal(HostState.Unreachable, seed.Targets[modem].State);
    }

    /// <summary>A target that has not finished its first sample window stays Starting
    /// for the test's whole life; the profile name is "Slow".</summary>
    private static void SlowFor(HomeOptions home, params string[] names)
    {
        home.Profiles["Slow"] = new PingProfileConfig
        {
            NormalIntervalSeconds = 3600, BurstIntervalSeconds = 3600, BurstMaxDurationSeconds = 0.2,
            PingTimeoutSeconds = 0.5, HistorySize = 10, MinUsableSample = 5,
        };
        foreach (var t in home.Targets.Where(t => names.Contains(t.Name)))
            t.Profile = "Slow";
    }

    [Fact]
    public async Task FromEngine_carries_determined_states_address_and_probe()
    {
        await using var h = EngineHarness.Home(home =>
        {
            home.Targets.Add(new TargetConfig { Name = "Mute", Address = "203.0.113.7", Profile = "Slow" });
            SlowFor(home, "Mute");
        });
        h.Pings.SetUp(h.Ip("Mute"), false);
        await h.StartAsync();
        await h.WaitVerdictAsync(VerdictState.Healthy);
        foreach (var name in new[] { "Router", "Modem", "Google", "Cloudflare", "Quad9", "Printer", "NAS" })
            await h.WaitStateAsync(name, HostState.Responding);
        Assert.True(h.Engine.Monitors[h.Id("Mute")].CurrentState is HostState.Initializing or HostState.Starting);

        var seed = WarmStartSeeder.FromEngine(h.Engine);

        Assert.Equal(VerdictState.Healthy, seed.Verdict);
        var seen = 0;
        foreach (var (id, monitor) in h.Engine.Monitors)
        {
            if (monitor.CurrentState is HostState.Initializing or HostState.Starting)
                continue;
            seen++;
            Assert.Equal(monitor.CurrentState, seed.Targets[id].State);
            Assert.Equal(monitor.Target.Address, seed.Targets[id].Address);
            Assert.Equal(monitor.Target.Probe, seed.Targets[id].Probe);
        }
        Assert.True(seen >= 7);
        Assert.Equal(seen, seed.Targets.Count);
        Assert.False(seed.Targets.ContainsKey(h.Id("Mute")));
        Assert.Equal("198.51.100.8", seed.Targets[h.Id("Google")].Address);
        Assert.Equal(HostState.Responding, seed.Pools.Single().Value);
    }

    [Fact]
    public async Task FromEngine_never_seeds_an_Unknown_verdict()
    {
        await using var h = EngineHarness.Home(home => SlowFor(home, "Google", "Cloudflare"));
        await h.StartAsync();
        await h.WaitStateAsync("Router", HostState.Responding);
        Assert.Equal(VerdictState.Unknown, h.Engine.Verdict?.CurrentVerdict);

        var seed = WarmStartSeeder.FromEngine(h.Engine);

        Assert.Null(seed.Verdict);
        Assert.True(seed.Targets.ContainsKey(h.Id("Router")));
    }

    private async Task RecordRollup(string resolution, DateTime periodStartUtc)
    {
        await using var db = _db.CreateDbContext();
        db.Rollups.Add(new RollupRow
        {
            TargetId = Guid.NewGuid(), PeriodStartUtc = periodStartUtc, Resolution = resolution,
            PingCount = 6, SuccessCount = 6, RttAvg = 10, RttMin = 9, RttMax = 11,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_recent_minute_rollup_keeps_a_quiet_target_warm()
    {
        // A quiet link records no transitions for days; the rollup collector is the only
        // heartbeat. Without it every restart of a stable link would cold-start.
        var router = await AddTarget("Router", "192.0.2.1");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddDays(-3));
        await RecordRollup(RollupResolution.Minute, now.AddMinutes(-2));

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(HostState.Responding, seed.Targets[router].State);
    }

    [Theory]
    [InlineData(RollupResolution.Hour)]
    [InlineData(RollupResolution.Day)]
    public async Task Only_minute_rollups_count(string resolution)
    {
        var router = await AddTarget("Router", "192.0.2.1");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddDays(-3));
        await RecordRollup(resolution, now.AddMinutes(-2));

        Assert.Null(await _seeder.FromDbAsync());
    }

    [Fact]
    public async Task A_rollup_is_alive_at_its_end()
    {
        // A minute rollup that STARTED just beyond the gap ENDED a minute later, inside it.
        var router = await AddTarget("Router", "192.0.2.1");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddDays(-3));
        await RecordRollup(RollupResolution.Minute,
            now.AddMinutes(-SettingsDefaults.WarmRestartMaxGapMinutes).AddSeconds(-30));

        Assert.NotNull(await _seeder.FromDbAsync());
    }
}
