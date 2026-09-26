using ConPersistence.Config;
using ConPersistence.Settings;

namespace ConPersistence.Tests;

public class ConfigHealthTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;
    private readonly ConfigService _service;
    private readonly SettingsStore _settings;
    private readonly ConfigHealth _health;

    public ConfigHealthTests()
    {
        _store = new ConfigStore(_factory);
        _service = new ConfigService(_factory, _store);
        _settings = new SettingsStore(_factory);
        _health = new ConfigHealth(_factory, _settings);
    }

    public async Task InitializeAsync()
    {
        await _store.SeedAsync(ConfigStoreTests.ValidHome()); // pool "Internet" = Google, Cloudflare
        await _settings.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.AnchorPool] = "Internet",
            [SettingKeys.WeatherThreshold] = "2",
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private async Task<List<string>> Codes() =>
        (await _health.EvaluateAsync()).Select(w => w.Code).ToList();

    [Fact]
    public async Task Two_anchors_threshold_two_warns_recommended_only()
    {
        var codes = await Codes();
        Assert.Equal(["anchors-below-recommended"], codes);
    }

    [Fact]
    public async Task Three_anchors_is_clean()
    {
        await _service.UpsertTargetAsync(new TargetDto { Name = "Quad9", Address = "9.9.9.9", Parent = "Modem" });
        var pool = (await _store.ExportAsync()).Pools.Single();
        await _service.UpsertPoolAsync(pool with { Members = ["Google", "Cloudflare", "Quad9"] });
        Assert.Empty(await Codes());
    }

    [Fact]
    public async Task One_anchor_warns_below_threshold_and_recommended()
    {
        var pool = (await _store.ExportAsync()).Pools.Single();
        await _service.UpsertPoolAsync(pool with { Members = ["Google"] });
        var codes = await Codes();
        Assert.Contains("anchors-below-threshold", codes);
        Assert.Contains("anchors-below-recommended", codes);
    }

    [Fact]
    public async Task No_anchor_pool_setting_warns_off()
    {
        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = null });
        Assert.Equal(["no-anchor-pool"], await Codes());
    }

    [Fact]
    public async Task Dangling_anchor_pool_name_warns_missing()
    {
        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = "Ghost" });
        Assert.Equal(["anchor-pool-missing"], await Codes());
    }

    [Fact]
    public async Task Reparenting_members_without_the_pool_warns_drift()
    {
        // The NAS incident: a new hop appears under Modem, the members move
        // behind it, the pool's parent silently stays Modem.
        await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "Edge", Address = "198.51.100.201", Parent = "Modem",
        });
        var doc = await _store.ExportAsync();
        foreach (var name in new[] { "Google", "Cloudflare" })
            await _service.UpsertTargetAsync(
                doc.Targets.Single(t => t.Name == name) with { Parent = "Edge" });

        var warnings = await _health.EvaluateAsync();
        var drift = Assert.Single(warnings, w => w.Code == "pool-parent-drift");
        Assert.Contains("'Edge'", drift.Message);
        Assert.Contains("'Modem'", drift.Message);

        // Re-parenting the pool clears it.
        var pool = (await _store.ExportAsync()).Pools.Single();
        await _service.UpsertPoolAsync(pool with { Parent = "Edge" });
        Assert.DoesNotContain("pool-parent-drift", await Codes());
    }

    [Fact]
    public async Task Unparented_pool_with_parented_members_warns_drift()
    {
        var pool = (await _store.ExportAsync()).Pools.Single();
        await _service.UpsertPoolAsync(pool with { Parent = null });
        var warnings = await _health.EvaluateAsync();
        var drift = Assert.Single(warnings, w => w.Code == "pool-parent-drift");
        Assert.Contains("not parented", drift.Message);
    }
}
