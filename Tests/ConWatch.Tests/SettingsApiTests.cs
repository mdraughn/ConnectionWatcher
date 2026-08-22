using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConWatch.Tests;

public class SettingsApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public SettingsApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<Dictionary<string, string>> GetSettings(HttpClient? client = null) =>
        (await (client ?? _client).GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings"))!;

    [Fact]
    public async Task Appsettings_values_seed_the_table_on_first_boot()
    {
        var settings = await GetSettings();
        Assert.Equal("Internet", settings["Verdict.AnchorPool"]);
        Assert.Equal("2", settings["Verdict.WeatherThreshold"]);
        Assert.Equal("90", settings["Retention.MinuteRetentionDays"]);
    }

    [Fact]
    public async Task Clearing_and_restoring_anchor_pool_toggles_the_verdict()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            var r = await _client.GetAsync("/api/v1/status");
            if (!r.IsSuccessStatusCode) return false;
            var s = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
            return s.GetProperty("verdict").ValueKind == JsonValueKind.Object;
        });

        var clear = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.AnchorPool"] = null });
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        await ConfigApiTests.Eventually(async () =>
        {
            var r = await _client.GetAsync("/api/v1/status");
            if (!r.IsSuccessStatusCode) return false;
            var s = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
            return s.GetProperty("verdict").ValueKind == JsonValueKind.Null;
        });

        var restore = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.AnchorPool"] = "Internet" });
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        await ConfigApiTests.Eventually(async () =>
        {
            var r = await _client.GetAsync("/api/v1/status");
            if (!r.IsSuccessStatusCode) return false;
            var s = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
            return s.GetProperty("verdict").ValueKind == JsonValueKind.Object;
        });
    }

    [Theory]
    [InlineData("Verdict.WeatherThreshold", "0")]
    [InlineData("Verdict.WeatherThreshold", "banana")]
    [InlineData("Retention.MinuteRetentionDays", "0")]
    [InlineData("Retention.RetentionRunHourUtc", "24")]
    [InlineData("Retention.LastRunUtc", "2026-01-01T00:00:00Z")]
    [InlineData("Nonsense.Key", "x")]
    public async Task Invalid_puts_are_400(string key, string value)
    {
        var response = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [key] = value });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Anchor_pool_must_name_an_existing_pool()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.AnchorPool"] = "NoSuchPool" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Retention_edit_survives_restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"conwatch-restart-{Guid.NewGuid():N}.db");
        try
        {
            // A real restart: the first instance is fully stopped before the
            // second boots on the same database file.
            using (var first = new ConWatchFactory(dbPath))
            {
                var put = await first.CreateClient().PutAsJsonAsync("/api/v1/settings",
                    new Dictionary<string, string?> { ["Retention.MinuteRetentionDays"] = "45" });
                Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            }

            using var second = new ConWatchFactory(dbPath);
            var settings = await GetSettings(second.CreateClient());
            Assert.Equal("45", settings["Retention.MinuteRetentionDays"]);
        }
        finally
        {
            try { File.Delete(dbPath); } catch (IOException) { }
        }
    }
}
