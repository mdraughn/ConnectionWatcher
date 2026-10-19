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
    [InlineData("Mqtt.Host", "homeassistant.local")]
    [InlineData("Mqtt.Port", "1883")]
    [InlineData("Mqtt.Enabled", "false")]
    [InlineData("Webhook.Urls", "http://example.com/hook\nhttps://other.example/x")]
    [InlineData("Webhook.VerdictOnly", "true")]
    [InlineData("Webhook.SkipExplained", "false")]
    [InlineData("Heartbeat.Url", "https://hc-ping.com/some-uuid")]
    [InlineData("Heartbeat.IntervalMinutes", "5")]
    public async Task Valid_notifier_settings_are_accepted(string key, string value)
    {
        var response = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [key] = value });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(value, (await GetSettings())[key]);
    }

    [Theory]
    [InlineData("Mqtt.Port", "70000")]
    [InlineData("Mqtt.Enabled", "yes")]
    [InlineData("Webhook.SkipExplained", "maybe")]
    [InlineData("Webhook.Urls", "not-a-url")]
    [InlineData("Webhook.Urls", "ftp://example.com/x")]
    [InlineData("Heartbeat.Url", "not-a-url")]
    [InlineData("Heartbeat.IntervalMinutes", "0")]
    [InlineData("Heartbeat.IntervalMinutes", "1441")]
    public async Task Invalid_notifier_settings_are_400(string key, string value)
    {
        var response = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [key] = value });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mqtt_test_requires_a_host()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/mqtt-test",
            new { host = (string?)null, port = 1883 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Mqtt_test_reports_unreachable_broker_as_ok_false()
    {
        // A closed local port refuses instantly — no broker needed.
        var response = await _client.PostAsJsonAsync("/api/v1/admin/mqtt-test",
            new { host = "127.0.0.1", port = 1 });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.False(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("message").GetString()!.Length > 0);
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
        // Own directory: the app keeps sibling files (admin-password.txt,
        // keys) beside the DB, and bare %TEMP% is shared with everything.
        var dir = Directory.CreateTempSubdirectory("conwatch-restart").FullName;
        var dbPath = Path.Combine(dir, "conwatch.db");
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
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Asn_lookup_setting_accepts_only_booleans()
    {
        var bad = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var good = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "true" });
        Assert.Equal(HttpStatusCode.NoContent, good.StatusCode);
        Assert.Equal("true", (await GetSettings())["Traceroute.AsnLookup"]);
    }

    private async Task<string?> VerdictStateAsync()
    {
        var response = await _client.GetAsync("/api/v1/status"); // 503 while the engine is (re)building
        if (!response.IsSuccessStatusCode) return null;
        var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var verdict = status.GetProperty("verdict");
        return verdict.ValueKind == JsonValueKind.Object ? verdict.GetProperty("state").GetString() : null;
    }

    /// <summary>One of the two pool anchors down. Whether that is a site incident or internet weather is
    /// exactly the Settings-table Verdict.WeatherThreshold (appsettings says 2 in the test factory).</summary>
    [Theory]
    [InlineData(1, "InternetWeather")]
    [InlineData(2, "SiteIncident")]
    public async Task Weather_threshold_from_settings_reaches_the_verdict(int threshold, string expected)
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await HostTestWait.Eventually(() => HostTestWait.SteadyStateAsync(_client));

        var put = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Verdict.WeatherThreshold"] = threshold.ToString() });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(threshold.ToString(), (await GetSettings())["Verdict.WeatherThreshold"]);

        // The rebuild the setting causes may land before or after the outage; the
        // fake keeps Google down either way, so the verdict converges on the answer.
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("8.8.8.8"), false);
        await HostTestWait.Eventually(async () => await VerdictStateAsync() == expected);

        if (threshold == 2)
        {
            // Control: with the default threshold one degraded anchor never becomes weather.
            await Task.Delay(1000); // absence window, after the positive condition above
            Assert.Equal("SiteIncident", await VerdictStateAsync());
        }
    }
}
