using System.Net.Http.Json;
using ConCore.Dns;
using ConCore.Targets;
using ConInfrastructure.Discovery;
using ConPersistence.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

public class DnsWizardTests : IDisposable
{
    private sealed class FakeGateway : IGatewayDiscovery
    {
        public Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GatewayCandidate>>([new GatewayCandidate("192.168.1.1", "default-route", true, 1)]);
    }
    private sealed class FakeWanProbe : IWanAddressProbe
    {
        public Task<WanProbeResult> ProbeAsync(CancellationToken ct) => Task.FromResult(new WanProbeResult("203.0.113.23", null, true, 12));
    }
    private sealed class Factory : ConWatchFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IGatewayDiscovery>(); s.AddSingleton<IGatewayDiscovery, FakeGateway>();
                s.RemoveAll<IWanAddressProbe>(); s.AddSingleton<IWanAddressProbe, FakeWanProbe>();
            });
        }
    }

    private readonly Factory _factory = new();
    private readonly HttpClient _client;
    public DnsWizardTests() => _client = _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    private async Task<List<TargetDto>> TargetsAsync() => (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;

    [Fact]
    public void Every_catalog_entry_names_a_seeded_lookup_name()
    {
        foreach (var entry in SetupService.AnchorCatalog)
            Assert.Contains(DnsLookupName.Seeded, n => n.Name == entry.LookupName);
    }

    [Fact]
    public async Task A_named_resolver_becomes_an_upstream_Resolver_target()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/setup/apply", new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = new[] { "8.8.8.8", "1.1.1.1" },
            upstreamResolverAddress = "1.1.1.1",
        });
        r.EnsureSuccessStatusCode();
        var upstream = Assert.Single(await TargetsAsync(), t => t.Scope == TargetScope.Resolver);
        Assert.Equal("1.1.1.1", upstream.Address);
        Assert.Equal("1.1.1.1 directly", upstream.Name);
        Assert.Equal("Resolver", upstream.Profile);
        Assert.Equal("dns", upstream.Probe);
        Assert.False(upstream.SystemManaged);
        Assert.Null(upstream.Parent);
    }

    [Fact]
    public async Task I_dont_know_picks_the_first_chosen_anchor_and_no_anchors_means_no_resolver()
    {
        var r = await _client.PostAsJsonAsync("/api/v1/setup/apply", new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = new[] { "9.9.9.9", "8.8.8.8" },
            upstreamResolverAddress = (string?)null,
        });
        r.EnsureSuccessStatusCode();
        Assert.Equal("9.9.9.9", Assert.Single(await TargetsAsync(), t => t.Scope == TargetScope.Resolver).Address);

        using var fresh = new Factory();
        var client = fresh.CreateClient();
        var r2 = await client.PostAsJsonAsync("/api/v1/setup/apply", new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = Array.Empty<string>(),
        });
        r2.EnsureSuccessStatusCode();
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!, t => t.Scope == TargetScope.Resolver);
    }

    [Fact]
    public async Task Rerunning_the_wizard_does_not_duplicate_the_resolver()
    {
        var body = new
        {
            router = new { name = "Router", address = "192.168.1.1" },
            anchorAddresses = new[] { "8.8.8.8" },
            upstreamResolverAddress = "8.8.8.8",
        };
        (await _client.PostAsJsonAsync("/api/v1/setup/apply", body)).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync("/api/v1/setup/apply", body)).EnsureSuccessStatusCode();
        Assert.Single(await TargetsAsync(), t => t.Scope == TargetScope.Resolver);
    }

    [Fact]
    public async Task The_wizard_page_has_the_resolver_step()
    {
        var html = await _client.GetStringAsync("/Setup");
        Assert.Contains("Which public resolver do you use?", html);
        Assert.Contains("This lets ConnectionWatcher tell your resolver from everyone's.", html);
        Assert.Contains("id=\"resolver-list\"", html);
    }
}
