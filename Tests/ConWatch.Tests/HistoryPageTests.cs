using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class HistoryPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static string Island(string html, string id)
    {
        var match = Regex.Match(html,
            $"<script id=\"{id}\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, $"no '{id}' island in the page");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Archive_mode_renders_the_same_subjects_and_roles_as_live_mode()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(() =>
            Task.FromResult(host.Engine is { Monitors.Count: 3, Verdict: not null }));
        var live = await client.GetStringAsync("/History");

        (await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" })).EnsureSuccessStatusCode();
        await ConfigApiTests.Eventually(() => Task.FromResult(host.Engine is null));
        var archive = await client.GetStringAsync("/History");

        Assert.Equal(Island(live, "history-data"), Island(archive, "history-data"));
        Assert.Equal(Island(live, "history-topology"), Island(archive, "history-topology"));
        Assert.Contains("\"role\":\"anchor\"", Island(live, "history-data"));
        Assert.Contains("\"name\":\"Internet (verdict)\"", Island(live, "history-data"));
        Assert.Contains("\"trunkNames\":[\"Router\"]", Island(live, "history-topology"));
    }
}
