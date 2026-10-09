using System.Net;
using System.Text.Json;
using ConEngine.Topology;
using ConPersistence.Recording;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class EndToEndTests : IDisposable
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress GoogleIp = IPAddress.Parse("8.8.8.8");
    private static readonly IPAddress CloudflareIp = IPAddress.Parse("1.1.1.1");

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public EndToEndTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<string?> Verdict()
    {
        var response = await _client.GetAsync("/api/v1/status");
        if (!response.IsSuccessStatusCode)
            return null;
        var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return status.GetProperty("verdict").ValueKind == JsonValueKind.Object
            ? status.GetProperty("verdict").GetProperty("state").GetString()
            : null;
    }

    [Fact]
    public async Task Verdict_walks_the_full_scenario_and_history_records_it()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () => await Verdict() == "Healthy");

        // One anchor down: with the ISP unknown, one answering anchor can't prove the internet up.
        _factory.Pings.SetUp(GoogleIp, false);
        await ConfigApiTests.Eventually(async () => await Verdict() == "LinkDegraded");

        // Both anchors down: nothing answers beyond the router.
        _factory.Pings.SetUp(CloudflareIp, false);
        await ConfigApiTests.Eventually(async () => await Verdict() == "LinkDown");

        // Router down too: now the router itself is blamed.
        _factory.Pings.SetUp(RouterIp, false);
        await ConfigApiTests.Eventually(async () => await Verdict() == "LinkDown");

        // Full recovery.
        _factory.Pings.SetUp(RouterIp, true);
        _factory.Pings.SetUp(GoogleIp, true);
        _factory.Pings.SetUp(CloudflareIp, true);
        await ConfigApiTests.Eventually(async () => await Verdict() == "Healthy");

        // The whole story is in the database, in order.
        await _factory.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
        var response = await _client.GetAsync(
            $"/api/v1/transitions?subjectId={VerdictCoordinator.SubjectId}&limit=100");
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var verdicts = json.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("verdict").GetString())
            .Reverse() // API is newest-first; we want chronological
            .ToList();

        // Chronological sequence must contain this exact progression.
        var expected = new[] { "Healthy", "LinkDegraded", "LinkDown", "Healthy" };
        int cursor = 0;
        foreach (var v in verdicts)
            if (cursor < expected.Length && v == expected[cursor])
                cursor++;
        Assert.True(cursor == expected.Length,
            $"Expected verdict progression {string.Join("->", expected)} but log was {string.Join("->", verdicts)}");
    }
}
