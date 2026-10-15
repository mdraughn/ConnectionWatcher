using System.Net.Http.Json;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class DnsPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public DnsPageTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedWithResolverAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_fallback_dashboard_lists_resolvers_under_the_Name_resolution_heading_not_in_the_tree()
    {
        await SeedWithResolverAsync();
        string html = "";
        await ConfigApiTests.Eventually(async () =>
        {
            html = await _client.GetStringAsync("/");
            return html.Contains("Name resolution") && html.Contains("Your resolver 10.0.0.53");
        });
        var fallback = html[html.IndexOf("id=\"dashboard-root\"", StringComparison.Ordinal)..];
        var heading = fallback.IndexOf("Name resolution", StringComparison.Ordinal);
        var tree = fallback.IndexOf("<ul class=\"cw-tree\">", StringComparison.Ordinal);
        Assert.True(tree >= 0 && tree < heading, "the verdict tree comes first");
        Assert.True(fallback.IndexOf("Your resolver 10.0.0.53", StringComparison.Ordinal) > heading,
            "the resolver is listed under the heading, never as a tree root");
    }

    [Fact]
    public async Task The_DNS_page_lists_system_resolvers_including_disabled_ones_and_the_seeded_names()
    {
        await SeedWithResolverAsync();
        var targets = await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets");
        var managed = targets!.Single(t => t.SystemManaged);
        (await _client.PutAsJsonAsync($"/api/v1/targets/{managed.Id}", managed with { Enabled = false })).EnsureSuccessStatusCode();

        var html = await _client.GetStringAsync("/Config/Dns");
        Assert.Contains("found from your network settings", html);
        Assert.Contains("Your resolver 10.0.0.53", html);
        Assert.Contains("data-resolver-enabled=\"false\"", html);      // the disabled one is still listed
        Assert.Contains("dns.google", html);
        Assert.Contains("Seeded names are checked against their known answers", html);
        Assert.Contains("href=\"/Config/Dns\"", await _client.GetStringAsync("/Config/Targets")); // the tab
    }

    [Fact]
    public async Task Adding_and_removing_an_upstream_resolver_and_a_lookup_name_round_trips_through_the_endpoints_the_page_uses()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        // Upstream resolver, as the page posts it.
        var created = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "1.1.1.1 directly", address = "1.1.1.1", profile = "Resolver", probe = "dns", scope = "Resolver",
        });
        created.EnsureSuccessStatusCode();
        var html = await _client.GetStringAsync("/Config/Dns");
        Assert.Contains("1.1.1.1 directly", html);

        // Lookup names: the page keeps seeded entries' expected addresses and appends plain names.
        var seeded = ConCore.Dns.DnsLookupName.Seeded;
        var kept = seeded.Where(n => n.Name != "dns.opendns.com").ToList();
        var json = ConCore.Dns.DnsLookupName.ToJson(kept.Append(new ConCore.Dns.DnsLookupName("router.lan", [])));
        (await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?> { ["Dns.LookupNames"] = json }))
            .EnsureSuccessStatusCode();
        html = await _client.GetStringAsync("/Config/Dns");
        Assert.DoesNotContain("dns.opendns.com", html);
        Assert.Contains("router.lan", html);
        var stored = ConCore.Dns.DnsLookupName.FromJson(
            (await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings"))!["Dns.LookupNames"]);
        Assert.Equal(["8.8.8.8", "8.8.4.4"], stored.Single(n => n.Name == "dns.google").Expected); // untouched
        Assert.Empty(stored.Single(n => n.Name == "router.lan").Expected);
    }

    [Fact]
    public async Task The_DNS_page_emits_the_parsed_lookup_name_list_never_the_raw_stored_setting()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        // Stored the way a snapshot import would: no validation on the way in.
        const string stored = """
            [{"name":"  dns.google  ","expected":["8.8.8.8","8.8.4.4","</script><script>alert(1)</script>"],
              "x":"</script><script>alert(2)</script>"},
             null,
             {"name":"router.lan"}]
            """;
        await _factory.Services.GetRequiredService<ConPersistence.Settings.SettingsStore>()
            .SetManyAsync(new Dictionary<string, string?> { ["Dns.LookupNames"] = stored });

        var html = await _client.GetStringAsync("/Config/Dns");
        const string prefix = "window.dnsLookupNames = ";
        var start = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, "the page emits the list");
        start += prefix.Length;
        var emitted = html[start..html.IndexOf(";</script>", start, StringComparison.Ordinal)];

        var expected = ConCore.Dns.DnsLookupName.ToJson(ConCore.Dns.DnsLookupName.FromJson(stored));
        Assert.Equal(expected, emitted);
        Assert.Equal("""[{"name":"dns.google","expected":["8.8.8.8","8.8.4.4"]},{"name":"router.lan"}]""", emitted);
        Assert.DoesNotContain("alert(", html);
    }
}
