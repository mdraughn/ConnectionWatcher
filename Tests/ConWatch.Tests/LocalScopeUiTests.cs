using System.Net.Http.Json;

namespace ConWatch.Tests;

public class LocalScopeUiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public LocalScopeUiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedWithLocalNasAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_fallback_dashboard_lists_Local_targets_under_the_LAN_heading_not_in_the_tree()
    {
        await SeedWithLocalNasAsync();
        string html = "";
        await ConfigApiTests.Eventually(async () =>
        {
            html = await _client.GetStringAsync("/");
            return html.Contains("How's my LAN") && html.Contains("NasBox");
        });

        var lan = html.IndexOf("How's my LAN", StringComparison.Ordinal);
        var tree = html.IndexOf("<ul class=\"cw-tree\">", StringComparison.Ordinal);
        Assert.True(tree >= 0 && tree < lan, "the verdict tree comes first");
        // The fallback region before the LAN heading never names the Local target
        // (the initial-data JSON island is above #dashboard-root and is excluded).
        var fallback = html[html.IndexOf("id=\"dashboard-root\"", StringComparison.Ordinal)..];
        Assert.True(fallback.IndexOf("NasBox", StringComparison.Ordinal) > fallback.IndexOf("How's my LAN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_Local_targets_the_fallback_has_no_LAN_heading()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        string html = "";
        await ConfigApiTests.Eventually(async () =>
        {
            html = await _client.GetStringAsync("/");
            return html.Contains("Router");
        });
        Assert.DoesNotContain("How's my LAN", html);
    }
}
