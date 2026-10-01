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
    [InlineData("/Config/Profiles", "Be polite")]           // built-in description in the table
    [InlineData("/Config/Targets", "option selected=\"selected\" title=\"Public internet sites")] // the Public default's title
    [InlineData("/Config/Pools", "Pools")]
    [InlineData("/Traceroute", "Traceroute")]
    [InlineData("/Settings", "Danger zone")]
    [InlineData("/Settings", "MQTT / Home Assistant")]
    [InlineData("/Settings", "Webhook URLs")]
    [InlineData("/Settings", "Heartbeat (watch the watchman)")]
    [InlineData("/Settings", "<h2>Settings</h2>")]
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
    public async Task Old_admin_url_redirects_permanently_to_settings()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        var response = await client.GetAsync("/Admin");
        Assert.Equal(System.Net.HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal("/Settings", response.Headers.Location?.ToString());
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
    public async Task Targets_pipeline_lists_parentless_anchors_and_parents_written_in_another_case()
    {
        var client = _factory.CreateClient();
        var profile = new ProfileDto
        {
            Name = "Fast", NormalIntervalSeconds = 1, BurstIntervalSeconds = 0.5,
            BurstMaxDurationSeconds = 5, HistorySize = 20, MinUsableSample = 5,
        };
        (await client.PostAsJsonAsync("/api/v1/profiles", profile)).EnsureSuccessStatusCode();
        foreach (var target in new TargetDto[]
        {
            new() { Name = "Router", Address = "192.168.1.1", Profile = "Fast" },
            new() { Name = "Google", Address = "8.8.8.8", Parent = "router", Profile = "Fast" },
            new() { Name = "Quad9", Address = "9.9.9.9", Profile = "Fast" },
        })
            (await client.PostAsJsonAsync("/api/v1/targets", target)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/pools", new PoolDto { Name = "Internet", Members = ["Google", "Quad9"] }))
            .EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/Config/Targets");
        var start = html.IndexOf("Path to the internet", StringComparison.Ordinal);
        Assert.True(start >= 0, "no pipeline section");
        var pipeline = html[start..html.IndexOf("<table", start, StringComparison.Ordinal)];
        Assert.Contains("Google", pipeline); // parent "router" still finds Router's chain
        Assert.Contains("Quad9", pipeline);  // no parent: reached directly
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
