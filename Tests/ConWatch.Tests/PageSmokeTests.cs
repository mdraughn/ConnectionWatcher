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
