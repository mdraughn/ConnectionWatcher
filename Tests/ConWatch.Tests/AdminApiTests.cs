using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class AdminApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public AdminApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> Stats()
    {
        var response = await _client.GetAsync("/api/v1/admin/stats");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stats_reflect_seeded_data()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var stats = await Stats();
        Assert.True(stats.GetProperty("fileSizeBytes").GetInt64() > 0);
        Assert.Equal(3, stats.GetProperty("tables").GetProperty("targets").GetInt32());
        Assert.Equal(1, stats.GetProperty("tables").GetProperty("pools").GetInt32());
        Assert.Equal(3, stats.GetProperty("tables").GetProperty("profiles").GetInt32()); // 2 builtins + Fast
    }

    [Fact]
    public async Task Clear_history_removes_measurements_keeps_config()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            await _factory.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
            var stats = await Stats();
            return stats.GetProperty("tables").GetProperty("transitions").GetInt32() > 0;
        });

        // The engine keeps running during this test, so new rows may land the
        // instant the clear finishes. The promise is: everything RECORDED SO
        // FAR is gone (and config survives) — assert by id watermark, not by
        // expecting the world to hold still.
        var lastBefore = (await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/v1/transitions?limit=1")).GetProperty("items")[0].GetProperty("id").GetInt64();

        var wrong = await _client.PostAsJsonAsync("/api/v1/admin/clear-history", new { confirm = "clear" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await _client.PostAsJsonAsync("/api/v1/admin/clear-history", new { confirm = "CLEAR" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var afterItems = (await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/v1/transitions?limit=500")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(afterItems, i => i.GetProperty("id").GetInt64() <= lastBefore);

        var after = await Stats();
        // Rollups: at most the freshly-flushed current minute since the clear.
        Assert.True(after.GetProperty("tables").GetProperty("rollups").GetInt32() <= 1);
        Assert.Equal(3, after.GetProperty("tables").GetProperty("targets").GetInt32());
    }

    [Fact]
    public async Task Factory_reset_wipes_everything_and_reseeds_builtins()
    {
        await ConfigApiTests.SeedHomeAsync(_client);

        var ok = await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var targets = await _client.GetFromJsonAsync<List<ConPersistence.Dto.TargetDto>>("/api/v1/targets");
        Assert.Empty(targets!);
        var profiles = await _client.GetFromJsonAsync<List<ConPersistence.Dto.ProfileDto>>("/api/v1/profiles");
        Assert.Equal(2, profiles!.Count); // builtins only
        var settings = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings");
        Assert.Equal("2", settings!["Verdict.WeatherThreshold"]); // defaults re-seeded
    }
}
