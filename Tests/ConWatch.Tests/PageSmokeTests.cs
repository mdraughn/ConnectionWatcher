using System.Net.Http.Json;
using System.Text.RegularExpressions;
using ConPersistence.Config;

namespace ConWatch.Tests;

public class PageSmokeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/", "Quick setup")] // empty config redirects to the wizard
    [InlineData("/History", "history-root")]
    [InlineData("/Config/Targets", "Targets")]
    [InlineData("/Config/Profiles", "Profiles")]
    [InlineData("/Config/Pools", "Pools")]
    [InlineData("/Traceroute", "Traceroute")]
    [InlineData("/Admin", "Danger zone")]
    [InlineData("/Admin", "MQTT / Home Assistant")]
    [InlineData("/Admin", "Webhook URLs")]
    [InlineData("/Admin", "Heartbeat (watch the watchman)")]
    [InlineData("/Data", "db-stats")]
    [InlineData("/Data", "Retention")]
    [InlineData("/Data", "Snapshot JSON")]
    [InlineData("/Setup", "Quick setup")]
    [InlineData("/Setup", "internet provider")]
    [InlineData("/Config/Targets", "ISP / operator")]
    [InlineData("/Report", "Internet connection report")]
    public async Task Page_renders(string url, string marker)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(marker, html);
    }

    [Fact]
    public async Task Targets_page_renders_pipeline_for_anchor_pool()
    {
        await ConfigApiTests.SeedHomeAsync(_factory.CreateClient());
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Contains("Path to the internet", html);
        Assert.Contains("data-insert-children", html);
        Assert.Contains("Internet anchors", html);
    }

    [Fact]
    public async Task Config_page_shows_health_banner_for_thin_anchor_pool()
    {
        // SeedHome makes a 2-member pool with threshold 2 -> below-recommended fires.
        await ConfigApiTests.SeedHomeAsync(_factory.CreateClient());
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Contains("cw-health", html);
        Assert.Contains("anchors-below-recommended", html);
    }

    [Fact]
    public async Task Targets_pipeline_draws_one_chain_per_anchor_parent()
    {
        var client = _factory.CreateClient();
        foreach (var target in new TargetDto[]
        {
            new() { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new() { Name = "WanA", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new() { Name = "WanB", Address = "192.168.200.1", Parent = "Router", Profile = "Internal" },
            new() { Name = "AnchorA", Address = "8.8.8.8", Parent = "WanA" },
            new() { Name = "AnchorB", Address = "1.1.1.1", Parent = "WanB" },
        })
            (await client.PostAsJsonAsync("/api/v1/targets", target)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/pools",
            new PoolDto { Name = "Internet", Members = ["AnchorA", "AnchorB"] })).EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/Config/Targets");

        Assert.Equal(2, Regex.Matches(html, "class=\"cw-pipeline\"").Count);
        Assert.Contains("<b>WanA</b>", html);
        Assert.Contains("<b>WanB</b>", html);
    }

    [Fact]
    public async Task Dashboard_embeds_initial_data_blob()
    {
        await ConfigApiTests.SeedHomeAsync(_factory.CreateClient());
        await ConfigApiTests.Eventually(async () =>
        {
            var html = await _factory.CreateClient().GetStringAsync("/");
            return html.Contains("initial-data") && html.Contains("Router");
        });
    }
}
