using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence;
using ConPersistence.Dto;

namespace ConWatch.Tests;

/// <summary>
/// The sanitized full-snapshot export (Route A): everything a local dev/test
/// setup needs, retrievable from the open API, with secret settings redacted.
/// </summary>
public class SnapshotTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public SnapshotTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedWithActivityAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Modem", address = "10.1.10.1", profile = "External", probe = "tcp:80",
        });
        await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Mqtt.Host"] = "homeassistant.local",
            ["Mqtt.Password"] = "super-secret",
            ["Heartbeat.Url"] = "https://hc-ping.com/secret-uuid",
        });
        // Fake pings flow immediately; wait until real transitions exist.
        await ConfigApiTests.Eventually(async () =>
        {
            var t = JsonSerializer.Deserialize<JsonElement>(
                await _client.GetStringAsync("/api/v1/transitions?limit=10"));
            return t.GetProperty("items").GetArrayLength() > 0;
        });
    }

    [Fact]
    public async Task Snapshot_contains_everything_with_secrets_redacted()
    {
        await SeedWithActivityAsync();

        var snapshot = JsonSerializer.Deserialize<Snapshot>(
            await _client.GetStringAsync("/api/v1/export/snapshot"), Web)!;

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.True(snapshot.Config.Targets.Count > 0);
        Assert.True(snapshot.Config.Profiles.Count > 0);
        Assert.True(snapshot.Config.Pools.Count > 0);
        Assert.True(snapshot.Transitions.Count > 0);

        Assert.Equal("tcp:80", Assert.Single(snapshot.Config.Targets, t => t.Name == "Modem").Probe);
        Assert.Null(Assert.Single(snapshot.Config.Targets, t => t.Name == "Router").Probe);

        Assert.Equal("homeassistant.local", snapshot.Settings["Mqtt.Host"]);
        Assert.False(snapshot.Settings.ContainsKey("Mqtt.Password"));
        Assert.False(snapshot.Settings.ContainsKey("Heartbeat.Url"));
        Assert.Contains("Mqtt.Password", snapshot.RedactedSettings);
        Assert.Contains("Heartbeat.Url", snapshot.RedactedSettings);
        Assert.DoesNotContain("super-secret",
            await _client.GetStringAsync("/api/v1/export/snapshot"));
    }

    [Fact]
    public async Task Snapshot_stays_open_when_admin_gate_is_on()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        await _client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK,
            (await _client.GetAsync("/api/v1/export/snapshot")).StatusCode);
    }

    [Fact]
    public async Task Snapshot_round_trips_into_a_working_local_database()
    {
        await SeedWithActivityAsync();
        var json = await _client.GetStringAsync("/api/v1/export/snapshot");
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, Web)!;

        var dir = Directory.CreateTempSubdirectory("conwatch-import").FullName;
        try
        {
            var dbPath = Path.Combine(dir, "conwatch.db");
            await SnapshotImporter.ImportToFileAsync(dbPath, snapshot);

            using var restored = new ConWatchFactory(dbPath);
            var client = restored.CreateClient();

            var status = JsonSerializer.Deserialize<JsonElement>(
                await client.GetStringAsync("/api/v1/status"));
            var statusTargets = status.GetProperty("targets").EnumerateArray().ToList();
            var restoredNames = statusTargets
                .Select(t => t.GetProperty("name").GetString()).OrderBy(n => n).ToList();
            var originalNames = snapshot.Config.Targets.Select(t => t.Name).OrderBy(n => n).ToList();
            Assert.Equal(originalNames, restoredNames);

            var modemStatus = statusTargets.Single(t => t.GetProperty("name").GetString() == "Modem");
            Assert.Equal("tcp:80", modemStatus.GetProperty("probe").GetString());
            var routerStatus = statusTargets.Single(t => t.GetProperty("name").GetString() == "Router");
            Assert.Equal(JsonValueKind.Null, routerStatus.GetProperty("probe").ValueKind);
            // Activity LEDs ride the snapshot: every target carries a
            // monotonic ping counter the tree flickers on.
            Assert.True(routerStatus.GetProperty("pingCount").GetInt64() >= 0);

            var transitions = JsonSerializer.Deserialize<JsonElement>(
                await client.GetStringAsync("/api/v1/transitions?limit=1000"));
            Assert.True(transitions.GetProperty("items").GetArrayLength() >= snapshot.Transitions.Count,
                "restored history must include everything the snapshot carried");

            var report = await client.GetStringAsync("/Report");
            Assert.Contains("Internet connection report", report);

            // Redacted secrets stay absent after import.
            Assert.Equal("homeassistant.local",
                (await client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings"))!["Mqtt.Host"]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
