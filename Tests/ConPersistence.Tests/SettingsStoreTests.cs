namespace ConPersistence.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly SettingsStore _store;
    private int _changeSignals;

    public SettingsStoreTests()
    {
        _store = new SettingsStore(_factory);
        _store.SettingsChanged += () => _changeSignals++;
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Set_get_roundtrip()
    {
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = "Internet" });
        Assert.Equal("Internet", await _store.GetAsync(SettingKeys.AnchorPool));
    }

    [Fact]
    public async Task GetInt_falls_back_on_missing_or_garbage()
    {
        Assert.Equal(2, await _store.GetIntAsync(SettingKeys.WeatherThreshold, 2));
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.WeatherThreshold] = "banana" });
        Assert.Equal(2, await _store.GetIntAsync(SettingKeys.WeatherThreshold, 2));
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.WeatherThreshold] = "3" });
        Assert.Equal(3, await _store.GetIntAsync(SettingKeys.WeatherThreshold, 2));
    }

    [Fact]
    public async Task SeedMissing_fills_only_absent_keys()
    {
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = "Internet" });
        await _store.SeedMissingAsync(new Dictionary<string, string>
        {
            [SettingKeys.AnchorPool] = "ShouldNotOverwrite",
            [SettingKeys.WeatherThreshold] = "2",
        });
        Assert.Equal("Internet", await _store.GetAsync(SettingKeys.AnchorPool));
        Assert.Equal("2", await _store.GetAsync(SettingKeys.WeatherThreshold));
    }

    [Fact]
    public async Task Verdict_writes_signal_change_but_retention_writes_do_not()
    {
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.MinuteRetentionDays] = "60" });
        Assert.Equal(0, _changeSignals);
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = "Internet" });
        Assert.Equal(1, _changeSignals);
    }

    [Fact]
    public async Task Null_value_deletes_key()
    {
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = "Internet" });
        await _store.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = null });
        Assert.Null(await _store.GetAsync(SettingKeys.AnchorPool));
    }

    [Fact]
    public async Task GetAll_returns_map()
    {
        await _store.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.AnchorPool] = "Internet",
            [SettingKeys.WeatherThreshold] = "2",
        });
        var all = await _store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal("Internet", all[SettingKeys.AnchorPool]);
    }
}
