using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ConWatch.Tests;

public class DiscoveriesPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public DiscoveriesPageTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task PostAsync(params object[] items) =>
        (await _client.PostAsJsonAsync("/api/v1/discoveries", new { items })).EnsureSuccessStatusCode();

    private static object Item(string address, string kind, string method = "ArpTable", string? hostname = null) =>
        new { address, hostname, kind, method, origin = "tray:PC", evidence = "test evidence" };

    private async Task<string> IdOfAsync(string address)
    {
        var all = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/discoveries?status=all"));
        return all.EnumerateArray().Single(d => d.GetProperty("address").GetString() == address)
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task The_page_lists_new_discoveries_with_kind_method_origin_and_evidence()
    {
        await PostAsync(Item("192.0.2.1", "Gateway", "DefaultRoute"), Item("192.0.2.50", "LocalDevice", hostname: "nas.example.com"));

        var html = await _client.GetStringAsync("/Config/Discoveries");

        Assert.Contains("192.0.2.1", html);
        Assert.Contains("Router (gateway)", html);
        Assert.Contains("Local device", html);
        Assert.Contains("nas.example.com", html);
        Assert.Contains("tray:PC", html);
        Assert.Contains("test evidence", html);
        Assert.Contains("Find my router", html);
        Assert.Contains("Find the network edge", html);
    }

    [Fact]
    public async Task Dismissed_ones_appear_only_in_the_dismissed_view_which_also_offers_Restore_all()
    {
        await PostAsync(Item("192.0.2.1", "Gateway"), Item("192.0.2.50", "LocalDevice"));
        await _client.PostAsync($"/api/v1/discoveries/{await IdOfAsync("192.0.2.50")}/dismiss", null);

        var main = await _client.GetStringAsync("/Config/Discoveries");
        var dismissed = await _client.GetStringAsync("/Config/Discoveries?status=dismissed");

        Assert.DoesNotContain("192.0.2.50", main);
        Assert.DoesNotContain("Restore all dismissed", main);
        Assert.Contains("192.0.2.50", dismissed);
        Assert.DoesNotContain("192.0.2.1<", dismissed);
        Assert.Contains("Restore all dismissed", dismissed);
        Assert.Contains("Restore", dismissed);
    }

    [Fact]
    public async Task An_unknown_status_falls_back_to_the_default_view()
    {
        await PostAsync(Item("192.0.2.1", "Gateway"));
        Assert.Contains("192.0.2.1", await _client.GetStringAsync("/Config/Discoveries?status=bogus"));
    }

    [Fact]
    public async Task Adopt_links_prefill_the_Targets_dialog_by_kind()
    {
        await PostAsync(Item("192.0.2.1", "Gateway", "DefaultRoute"),
            Item("203.0.113.5", "NetworkEdge", "Traceroute"),
            Item("192.0.2.50", "LocalDevice", hostname: "nas.example.com"));

        var html = System.Net.WebUtility.HtmlDecode(await _client.GetStringAsync("/Config/Discoveries"));

        Assert.Contains($"/Config/Targets?address=192.0.2.1&name=Router&profile=Internal&scope=Verdict&discoveryId={await IdOfAsync("192.0.2.1")}", html);
        Assert.Contains($"address=203.0.113.5&name=ISP%20edge&profile=Path&scope=Verdict&discoveryId={await IdOfAsync("203.0.113.5")}", html);
        Assert.Contains($"address=192.0.2.50&name=nas.example.com&profile=Local&scope=Local&discoveryId={await IdOfAsync("192.0.2.50")}", html);
    }

    [Fact]
    public async Task The_suggested_name_is_made_unique_against_existing_targets()
    {
        await ConfigApiTests.SeedHomeAsync(_client);          // already has a target named Router
        await PostAsync(Item("192.0.2.1", "Gateway", "DefaultRoute"));

        var html = System.Net.WebUtility.HtmlDecode(await _client.GetStringAsync("/Config/Discoveries"));

        Assert.Contains("name=Router%202&", html);
    }

    [Fact]
    public async Task An_adopted_discovery_names_its_target_and_a_removed_target_reads_target_removed()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await PostAsync(Item("192.0.2.50", "LocalDevice"));
        var id = await IdOfAsync("192.0.2.50");
        var created = await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={id}",
            new { name = "NasBox", address = "192.0.2.50", profile = "Fast", scope = "Local" });
        var targetId = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync()).GetProperty("id").GetString();

        var adopted = await _client.GetStringAsync("/Config/Discoveries?status=adopted");
        Assert.Contains("NasBox", adopted);
        Assert.DoesNotContain("target removed", adopted);

        (await _client.DeleteAsync($"/api/v1/targets/{targetId}")).EnsureSuccessStatusCode();
        var removed = await _client.GetStringAsync("/Config/Discoveries?status=adopted");
        Assert.Contains("target removed", removed);
    }

    [Fact]
    public async Task The_Config_tabs_link_to_Discoveries_and_mark_it_active_on_its_own_page()
    {
        var targets = await _client.GetStringAsync("/Config/Targets");
        Assert.Contains("href=\"/Config/Discoveries\"", targets);
        var page = await _client.GetStringAsync("/Config/Discoveries");
        Assert.Contains("class=\"active\" href=\"/Config/Discoveries\"", page);
    }

    [Fact]
    public async Task The_Targets_page_carries_a_valid_discoveryId_into_its_script_and_ignores_junk()
    {
        var id = Guid.NewGuid();
        var withId = await _client.GetStringAsync($"/Config/Targets?address=192.0.2.50&name=Cam&scope=Local&discoveryId={id}");
        Assert.Contains($"const adoptDiscoveryId = \"{id}\";", withId);

        var junk = await _client.GetStringAsync("/Config/Targets?address=192.0.2.50&discoveryId=not-a-guid");
        Assert.Contains("const adoptDiscoveryId = null;", junk);
        var none = await _client.GetStringAsync("/Config/Targets");
        Assert.Contains("const adoptDiscoveryId = null;", none);
    }

    [Fact]
    public async Task The_page_is_locked_behind_the_admin_gate()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await anon.GetAsync("/Config/Discoveries");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?admin=1", response.Headers.Location?.OriginalString);
    }
}
