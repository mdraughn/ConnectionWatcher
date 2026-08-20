namespace ConWatch.Tests;

public class PageSmokeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/", "dashboard-root")]
    [InlineData("/History", "history-root")]
    [InlineData("/Config/Targets", "Targets")]
    [InlineData("/Config/Profiles", "Profiles")]
    [InlineData("/Config/Pools", "Pools")]
    [InlineData("/Traceroute", "Traceroute")]
    public async Task Page_renders(string url, string marker)
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(marker, html);
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
