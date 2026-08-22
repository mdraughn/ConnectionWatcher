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

        var wrong = await _client.PostAsJsonAsync("/api/v1/admin/clear-history", new { confirm = "clear" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await _client.PostAsJsonAsync("/api/v1/admin/clear-history", new { confirm = "CLEAR" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var after = await Stats();
        Assert.Equal(0, after.GetProperty("tables").GetProperty("transitions").GetInt32());
        Assert.Equal(0, after.GetProperty("tables").GetProperty("rollups").GetInt32());
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
