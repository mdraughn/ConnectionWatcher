using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Targets;
using ConInfrastructure.Discovery;
using ConPersistence.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

public class SetupApiTests : IDisposable
{
    private sealed class FakeGateway : IGatewayDiscovery
    {
        public Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GatewayCandidate>>(
            [
                new GatewayCandidate("192.168.1.1", "default-route", true, 1),
                new GatewayCandidate("10.0.0.1", "probe", false, null),
            ]);
    }

    private sealed class FakeWanProbe : IWanAddressProbe
    {
        public Task<WanProbeResult> ProbeAsync(CancellationToken ct) =>
            Task.FromResult(new WanProbeResult("203.0.113.23", null, true, 12));
    }

    private sealed class Factory : ConWatchFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IGatewayDiscovery>();
                services.AddSingleton<IGatewayDiscovery, FakeGateway>();
                services.RemoveAll<IWanAddressProbe>();
                services.AddSingleton<IWanAddressProbe, FakeWanProbe>();
            });
        }
    }

    private readonly Factory _factory = new();
    private readonly HttpClient _client;

    public SetupApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static SetupTarget Router => new("Router", "192.168.1.1");

    [Fact]
    public async Task Gateway_and_wan_endpoints_pass_through()
    {
        var gateways = await _client.GetFromJsonAsync<JsonElement>("/api/v1/setup/gateway");
        Assert.Equal(2, gateways.GetArrayLength());
        Assert.Equal("192.168.1.1", gateways[0].GetProperty("address").GetString());

        var wan = await _client.PostAsync("/api/v1/setup/wan-probe", null);
        wan.EnsureSuccessStatusCode();
        var probe = JsonSerializer.Deserialize<JsonElement>(await wan.Content.ReadAsStringAsync());
        Assert.Equal("203.0.113.23", probe.GetProperty("address").GetString());
        Assert.True(probe.GetProperty("pingOk").GetBoolean());
    }

    [Fact]
    public async Task Anchor_catalog_lists_four_resolvers()
    {
        var anchors = await _client.GetFromJsonAsync<List<SetupTarget>>("/api/v1/setup/anchors");
        Assert.Equal(4, anchors!.Count);
        Assert.Contains(anchors, a => a.Address == "9.9.9.9");
    }

    [Fact]
    public async Task Apply_builds_router_anchors_pool_and_settings()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8", "1.1.1.1"]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Equal(4, targets!.Count); // Router + 2 anchors + the upstream resolver
        Assert.Null(targets.Single(t => t.Name == "Router").Parent);
        Assert.Equal("Router", targets.Single(t => t.Address == "8.8.8.8" && t.Scope == TargetScope.Verdict).Parent);
        Assert.True(targets.Single(t => t.Name == "Router").Critical);
        Assert.All(targets.Where(t => t.Name != "Router"), t => Assert.False(t.Critical));

        var pools = await _client.GetFromJsonAsync<List<PoolDto>>("/api/v1/pools");
        Assert.Equal(2, Assert.Single(pools!).Members.Count);

        var settings = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings");
        Assert.Equal("Internet", settings!["Verdict.AnchorPool"]);
    }

    [Fact]
    public async Task Apply_does_not_mark_an_existing_router_target_critical()
    {
        // D6: existing installs are unchanged; the wizard reuses a target at the router's address untouched.
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "My Router", address = "192.168.1.1", profile = "Internal",
        })).EnsureSuccessStatusCode();

        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8", "1.1.1.1"]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var targets = (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;
        Assert.False(targets.Single(t => t.Address == "192.168.1.1").Critical);
    }

    [Fact]
    public async Task Apply_is_additive_on_rerun()
    {
        await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8"]));
        var again = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8", "9.9.9.9"]));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Equal(4, targets!.Count); // Router + 2 anchors + one upstream resolver, no duplicates
        var pools = await _client.GetFromJsonAsync<List<PoolDto>>("/api/v1/pools");
        Assert.Equal(2, Assert.Single(pools!).Members.Count);
    }

    [Fact]
    public async Task Apply_with_outer_ip_chains_anchors_under_it()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, new SetupTarget("Outer IP", "203.0.113.23"), ["8.8.8.8"]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var targets = (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;
        Assert.Equal("Router", targets.Single(t => t.Name == "Outer IP").Parent);
        Assert.Equal("Outer IP", targets.Single(t => t.Address == "8.8.8.8" && t.Scope == TargetScope.Verdict).Parent);
    }

    [Fact]
    public async Task Apply_tags_catalog_anchors_with_their_operators()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8", "1.1.1.1", "203.0.113.9"]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var targets = (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;
        Assert.Equal("Google", targets.Single(t => t.Address == "8.8.8.8" && t.Scope == TargetScope.Verdict).Operator);
        Assert.Equal("Cloudflare", targets.Single(t => t.Address == "1.1.1.1" && t.Scope == TargetScope.Verdict).Operator);
        Assert.Null(targets.Single(t => t.Address == "203.0.113.9").Operator); // not in the catalog
    }

    [Fact]
    public async Task Apply_tags_the_wan_target_with_the_isp_name()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, new SetupTarget("Outer IP", "203.0.113.23"),
                ["8.8.8.8"], IspName: "Comcast"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var targets = (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;
        Assert.Equal("Comcast", targets.Single(t => t.Name == "Outer IP").Operator);
        Assert.Null(targets.Single(t => t.Name == "Router").Operator); // Mark's own gear
    }

    [Fact]
    public async Task First_boot_redirects_dashboard_to_wizard_until_setup_runs()
    {
        // Client follows the redirect: empty config lands on the wizard.
        var beforeHtml = await _client.GetStringAsync("/");
        Assert.Contains("Quick setup", beforeHtml);

        var apply = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8", "1.1.1.1"]));
        Assert.Equal(HttpStatusCode.NoContent, apply.StatusCode);

        await ConfigApiTests.Eventually(async () =>
        {
            var html = await _client.GetStringAsync("/");
            return html.Contains("dashboard-root");
        });
    }

    [Fact]
    public async Task Apply_without_router_address_is_400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(new SetupTarget("Router", ""), null, []));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Apply_is_all_or_nothing()
    {
        // The empty anchor address fails validation after the router and the
        // first anchor were built: nothing may be left behind.
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8", ""]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Empty(targets!);
    }

    [Fact]
    public async Task Apply_raises_one_config_change()
    {
        var config = _factory.Services.GetRequiredService<ConfigService>();
        var changes = 0;
        config.ConfigChanged += () => Interlocked.Increment(ref changes);

        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, new SetupTarget("WAN", "203.0.113.23"), ["8.8.8.8", "1.1.1.1"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Apply_assigns_Internal_Path_and_Public_by_role()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, new SetupTarget("WAN", "203.0.113.23"), ["8.8.8.8"]));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Equal("Internal", targets!.Single(t => t.Name == "Router").Profile);
        Assert.Equal("Path", targets!.Single(t => t.Address == "203.0.113.23").Profile);
        Assert.Equal("Public", targets!.Single(t => t.Address == "8.8.8.8" && t.Scope == TargetScope.Verdict).Profile);
    }
}
