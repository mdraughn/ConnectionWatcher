using ConCore.Dns;
using ConCore.Targets;
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

    /// <summary>The anchor-pool codes only: these fixtures have no resolvers, so the DNS codes ride along and are tested on their own.</summary>
    private async Task<List<string>> AnchorCodes() =>
        (await Codes()).Where(c => c is not ("dns-not-watched" or "no-upstream-resolver")).ToList();

    [Fact]
    public async Task Two_anchors_threshold_two_warns_recommended_only()
    {
        var codes = await AnchorCodes();
        Assert.Equal(["anchors-below-recommended"], codes);
    }

    [Fact]
    public async Task Three_anchors_is_clean()
    {
        await _service.UpsertTargetAsync(new TargetDto { Name = "Quad9", Address = "9.9.9.9", Parent = "Modem" });
        var pool = (await _store.ExportAsync()).Pools.Single();
        await _service.UpsertPoolAsync(pool with { Members = ["Google", "Cloudflare", "Quad9"] });
        Assert.Empty(await AnchorCodes());
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
        Assert.Equal(["no-anchor-pool"], await AnchorCodes());
    }

    [Fact]
    public async Task Dangling_anchor_pool_name_warns_missing()
    {
        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.AnchorPool] = "Ghost" });
        Assert.Equal(["anchor-pool-missing"], await AnchorCodes());
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

    // UpsertTargetAsync updates by Id (a null Id inserts, and a duplicate name fails),
    // so tag the exported targets: the same pattern as the re-parenting test above.
    private async Task<TargetDto> Existing(string name) =>
        (await _store.ExportAsync()).Targets.Single(t => t.Name == name);

    [Fact]
    public async Task Every_anchor_tagged_as_the_isp_warns_unprovable()
    {
        await _service.UpsertTargetAsync((await Existing("Modem")) with { Operator = "Comcast" });
        await _service.UpsertTargetAsync((await Existing("Google")) with { Operator = " comcast" });
        await _service.UpsertTargetAsync((await Existing("Cloudflare")) with { Operator = "Comcast" });

        var warning = (await _health.EvaluateAsync()).Single(w => w.Code == "anchors-all-isp");
        Assert.Contains("Tag your ISP on a path hop (or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up.",
            warning.Message);
    }

    [Fact]
    public async Task One_anchor_outside_the_isp_is_enough()
    {
        await _service.UpsertTargetAsync((await Existing("Modem")) with { Operator = "Comcast" });
        await _service.UpsertTargetAsync((await Existing("Google")) with { Operator = "Comcast" });
        Assert.DoesNotContain("anchors-all-isp", await Codes()); // Cloudflare is untagged
    }

    [Fact]
    public async Task No_isp_tag_on_the_path_never_warns_unprovable()
    {
        await _service.UpsertTargetAsync((await Existing("Google")) with { Operator = "Comcast" });
        await _service.UpsertTargetAsync((await Existing("Cloudflare")) with { Operator = "Comcast" });
        Assert.DoesNotContain("anchors-all-isp", await Codes()); // the path itself is untagged
    }

    [Fact]
    public async Task No_upstream_resolver_and_a_disabled_watch_are_health_warnings()
    {
        // ValidHome has no Resolver targets at all: nothing is watched yet (no system resolver found).
        Assert.Contains("dns-not-watched", await Codes());

        // A system resolver and the seeded names: watched, but no upstream resolver is named.
        await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto
            {
                Name = "Your resolver 192.168.1.1", Address = "192.168.1.1", Profile = "Resolver",
                Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true,
            });
            return ConfigResult.Ok();
        });
        await _settings.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.DnsLookupNames] = DnsLookupName.ToJson(DnsLookupName.Seeded),
        });
        var codes = await Codes();
        Assert.Contains("no-upstream-resolver", codes);
        Assert.DoesNotContain("dns-not-watched", codes);

        // An upstream resolver silences that; turning the watch off replaces it with dns-not-watched.
        await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "1.1.1.1 directly", Address = "1.1.1.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver });
            return ConfigResult.Ok();
        });
        Assert.DoesNotContain("no-upstream-resolver", await Codes());

        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.DnsEnabled] = "false" });
        codes = await Codes();
        Assert.Contains("dns-not-watched", codes);
        Assert.DoesNotContain("no-upstream-resolver", codes);
    }
}
