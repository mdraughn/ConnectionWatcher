using ConEngine.Configuration;
using ConEngine.Topology;
using ConPersistence.Config;

namespace ConPersistence.Tests;

public class ConfigStoreTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;

    public ConfigStoreTests() => _store = new ConfigStore(_factory);

    public void Dispose() => _factory.Dispose();

    internal static WatcherOptions ValidHome() => new()
    {
        Profiles = { ["Slow"] = new PingProfileConfig { NormalIntervalSeconds = 30 } },
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
            new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem", Profile = "Slow" },
        ],
        Pools =
        [
            new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"], Parent = "Modem" },
        ],
    };

    [Fact]
    public async Task Seed_with_null_creates_builtin_profiles_only()
    {
        Assert.True(await _store.IsEmptyAsync());
        await _store.SeedAsync(null);
        Assert.False(await _store.IsEmptyAsync());

        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Empty(options.Targets);
        Assert.Empty(options.Pools);
        Assert.Equal(2, options.Profiles.Count);
        Assert.Equal(1.0, options.Profiles["Internal"].NormalIntervalSeconds);
        Assert.Equal(10.0, options.Profiles["External"].NormalIntervalSeconds);
    }

    [Fact]
    public async Task Seed_with_home_config_roundtrips()
    {
        await _store.SeedAsync(ValidHome());
        var options = await _store.LoadWatcherOptionsAsync();

        Assert.Equal(4, options.Targets.Count);
        Assert.All(options.Targets, t => Assert.NotNull(t.Id));
        var modem = options.Targets.Single(t => t.Name == "Modem");
        Assert.Equal("Router", modem.Parent);
        Assert.Equal("Internal", modem.Profile);
        var cloudflare = options.Targets.Single(t => t.Name == "Cloudflare");
        Assert.Equal("Slow", cloudflare.Profile);
        Assert.Equal(30.0, options.Profiles["Slow"].NormalIntervalSeconds);

        var pool = Assert.Single(options.Pools);
        Assert.NotNull(pool.Id);
        Assert.Equal(["Google", "Cloudflare"], pool.Members);
        Assert.Equal("Modem", pool.Parent);
    }

    [Fact]
    public async Task Loaded_options_build_identical_graphs_twice()
    {
        await _store.SeedAsync(ValidHome());
        var options = await _store.LoadWatcherOptionsAsync();
        var g1 = TargetGraph.FromOptions(options);
        var g2 = TargetGraph.FromOptions(await _store.LoadWatcherOptionsAsync());
        Assert.Equal(g1.GetByName("Google").Id, g2.GetByName("Google").Id);
        Assert.Equal(g1.GetPoolByName("Internet").Id, g2.GetPoolByName("Internet").Id);
    }

    [Fact]
    public async Task Export_carries_names_refs_and_ids()
    {
        await _store.SeedAsync(ValidHome());
        var doc = await _store.ExportAsync();

        Assert.Contains(doc.Profiles, p => p.Name == "Internal");
        Assert.Contains(doc.Profiles, p => p.Name == "Slow");
        var google = doc.Targets.Single(t => t.Name == "Google");
        Assert.NotNull(google.Id);
        Assert.Equal("Modem", google.Parent);
        Assert.Equal("External", google.Profile);
        var pool = Assert.Single(doc.Pools);
        Assert.Equal(["Google", "Cloudflare"], pool.Members);
    }

    [Fact]
    public async Task Seed_normalizes_probe_and_operator_like_every_other_write_path()
    {
        var home = ValidHome();
        home.Targets[0].Probe = "TCP:80";
        home.Targets[0].Operator = "  Comcast ";
        home.Targets[1].Operator = "   ";
        await _store.SeedAsync(home);

        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Equal("tcp:80", options.Targets.Single(t => t.Name == "Router").Probe);
        Assert.Equal("Comcast", options.Targets.Single(t => t.Name == "Router").Operator);
        Assert.Null(options.Targets.Single(t => t.Name == "Modem").Operator);
    }

    [Fact]
    public async Task Seed_with_invalid_config_falls_back_to_builtins()
    {
        var bad = ValidHome();
        bad.Targets[0].Parent = "Ghost";
        await _store.SeedAsync(bad);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Empty(options.Targets);
        Assert.Equal(2, options.Profiles.Count);
    }
}
