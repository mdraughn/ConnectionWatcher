using ConPersistence.Dto;

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
}
