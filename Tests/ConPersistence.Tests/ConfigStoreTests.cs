using Microsoft.EntityFrameworkCore;
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
    public async Task Has_user_targets_ignores_system_managed_ones()
    {
        var service = new ConfigService(_factory, _store);
        Assert.False(await _store.HasUserTargetsAsync());
        await _store.SeedAsync(null);
        Assert.False(await _store.HasUserTargetsAsync());

        await service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto
            {
                Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Resolver",
                Probe = "dns", Scope = ConCore.Targets.TargetScope.Resolver, SystemManaged = true,
            });
            return ConfigResult.Ok();
        });
        Assert.False(await _store.HasUserTargetsAsync());

        await service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "Router", Address = "192.168.1.1", Profile = "Internal" });
            return ConfigResult.Ok();
        });
        Assert.True(await _store.HasUserTargetsAsync());
    }

    [Fact]
    public async Task Seed_with_null_creates_builtin_profiles_only()
    {
        Assert.True(await _store.IsEmptyAsync());
        await _store.SeedAsync(null);
        Assert.False(await _store.IsEmptyAsync());

        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Empty(options.Targets);
        Assert.Empty(options.Pools);
        Assert.Equal(5, options.Profiles.Count);
        Assert.Equal(1.0, options.Profiles["Internal"].NormalIntervalSeconds);
        Assert.Equal(10.0, options.Profiles["Public"].NormalIntervalSeconds);
        Assert.Equal(5.0, options.Profiles["Path"].NormalIntervalSeconds);
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
    public async Task An_edited_builtin_profile_reaches_the_engine()
    {
        await _store.SeedAsync(ValidHome());
        var internalProfile = (await _store.ExportAsync()).Profiles.Single(p => p.Name == "Internal");
        var result = await new ConfigService(_factory, _store)
            .UpsertProfileAsync(internalProfile with { NormalIntervalSeconds = 2.5 });
        Assert.True(result.Success);

        var graph = TargetGraph.FromOptions(await _store.LoadWatcherOptionsAsync());

        Assert.Equal(TimeSpan.FromSeconds(2.5), graph.ProfileFor(graph.GetByName("Router")).NormalInterval);
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
        Assert.Equal("Public", google.Profile);
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
        Assert.Equal(5, options.Profiles.Count);
    }

    [Fact]
    public async Task Seed_gives_every_builtin_a_description()
    {
        await _store.SeedAsync(null);
        var doc = await _store.ExportAsync();
        Assert.Equal(5, doc.Profiles.Count);
        Assert.All(doc.Profiles, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
        Assert.Contains("LAN", doc.Profiles.Single(p => p.Name == "Internal").Description);
    }

    [Fact]
    public async Task EnsureBuiltIns_adds_missing_presets_and_fills_null_descriptions_only()
    {
        // A database from before Path existed: Internal with no description, a custom profile.
        await using (var db = _factory.CreateDbContext())
        {
            db.Profiles.Add(new ConPersistence.Entities.ProfileRow
                { Id = Guid.NewGuid(), Name = "Internal", NormalIntervalSeconds = 2, Description = null });
            db.Profiles.Add(new ConPersistence.Entities.ProfileRow
                { Id = Guid.NewGuid(), Name = "Public", Description = "mine" });
            db.Profiles.Add(new ConPersistence.Entities.ProfileRow
                { Id = Guid.NewGuid(), Name = "Slow", NormalIntervalSeconds = 30 });
            await db.SaveChangesAsync();
        }

        await _store.EnsureBuiltInsAsync();

        var doc = await _store.ExportAsync();
        Assert.Equal(["Internal", "Local", "Path", "Public", "Resolver", "Slow"], doc.Profiles.Select(p => p.Name).OrderBy(n => n));
        var internalProfile = doc.Profiles.Single(p => p.Name == "Internal");
        Assert.Equal(2, internalProfile.NormalIntervalSeconds); // numbers never overwritten
        Assert.Contains("LAN", internalProfile.Description);     // null description filled
        Assert.Equal("mine", doc.Profiles.Single(p => p.Name == "Public").Description); // kept
        Assert.Equal(5, doc.Profiles.Single(p => p.Name == "Path").NormalIntervalSeconds);
        Assert.Null(doc.Profiles.Single(p => p.Name == "Slow").Description);
    }

    [Fact]
    public async Task EnsureBuiltIns_adds_the_Resolver_preset_to_an_older_database()
    {
        await _store.SeedAsync(null);
        await using (var db = _factory.CreateDbContext())
        {
            db.Profiles.Remove(await db.Profiles.SingleAsync(p => p.Name == "Resolver"));
            await db.SaveChangesAsync();
        }

        await _store.EnsureBuiltInsAsync();

        await using var check = _factory.CreateDbContext();
        var row = await check.Profiles.SingleAsync(p => p.Name == "Resolver");
        Assert.Equal(30, row.NormalIntervalSeconds);
        Assert.False(string.IsNullOrEmpty(row.Description));
    }
}
