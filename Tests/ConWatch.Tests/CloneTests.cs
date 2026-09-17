using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConWatch.Clone;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// Clone + take-over across two in-memory nodes: node B's outbound "clone"
/// HttpClient is routed at node A's TestServer, so real server-to-server
/// choreography runs without sockets.
/// </summary>
public class CloneTests : IDisposable
{
    private readonly ConWatchFactory _source = new();
    private readonly HttpClient _sourceClient;
    private ConWatchFactory? _target;

    public CloneTests() => _sourceClient = _source.CreateClient();

    public void Dispose()
    {
        _target?.Dispose();
        _source.Dispose();
    }

    private ConWatchFactory MakeTarget()
    {
        _target = new ConWatchFactory
        {
            TestServices = services => services
                .AddHttpClient(CloneService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => _source.Server.CreateHandler()),
        };
        return _target;
    }

    private static async Task<JsonElement> GetJson(HttpClient client, string url)
    {
        // No EnsureSuccessStatusCode: health polls legitimately see 503 while
        // an engine is still starting, and its body carries the same shape.
        var response = await client.GetAsync(url);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private async Task SeedSourceAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_sourceClient);
        var secrets = await _sourceClient.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?>
            {
                ["Mqtt.Password"] = "broker-secret",
                ["Heartbeat.Url"] = "https://hc-ping.com/test-uuid",
            });
        Assert.Equal(HttpStatusCode.NoContent, secrets.StatusCode);
        await ConfigApiTests.Eventually(async () =>
            (await GetJson(_sourceClient, "/api/v1/health"))
                .GetProperty("status").GetString() == "Healthy");
    }

    [Fact]
    public async Task Full_snapshot_carries_secrets_and_verifies_password_inline()
    {
        await SeedSourceAsync();

        // Gate open: full snapshot needs no password; secrets ride along.
        var open = await _sourceClient.PostAsJsonAsync("/api/v1/export/snapshot/full", new { });
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        var full = JsonSerializer.Deserialize<JsonElement>(await open.Content.ReadAsStringAsync());
        Assert.Equal("broker-secret", full.GetProperty("settings").GetProperty("Mqtt.Password").GetString());
        Assert.Equal(0, full.GetProperty("redactedSettings").GetArrayLength());

        // The sanitized snapshot still redacts.
        var sanitized = await GetJson(_sourceClient, "/api/v1/export/snapshot");
        Assert.False(sanitized.GetProperty("settings").TryGetProperty("Mqtt.Password", out _));

        // Gate set: wrong password 401; right password 200.
        (await _sourceClient.PostAsJsonAsync("/api/v1/auth/set-password",
            new { password = "hunter2" })).EnsureSuccessStatusCode();
        var anon = _source.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PostAsJsonAsync("/api/v1/export/snapshot/full",
                new { password = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anon.PostAsJsonAsync("/api/v1/export/snapshot/full",
                new { password = "hunter2" })).StatusCode);
    }

    [Fact]
    public async Task Clone_arrives_as_disabled_archive_with_data_and_secrets()
    {
        await SeedSourceAsync();
        var target = MakeTarget();
        var client = target.CreateClient();

        // http + non-loopback + secrets: refused without the explicit override.
        var refused = await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = false,
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("insecure-transport", await refused.Content.ReadAsStringAsync());

        var cloned = await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        });
        Assert.Equal(HttpStatusCode.OK, cloned.StatusCode);
        var summary = JsonSerializer.Deserialize<JsonElement>(await cloned.Content.ReadAsStringAsync());
        Assert.Equal(3, summary.GetProperty("targets").GetInt32());
        Assert.True(summary.GetProperty("secretsTransferred").GetBoolean());

        // Disabled archive: health says so, config and history are there,
        // secrets came across, and the source URL is recorded.
        var health = await GetJson(client, "/api/v1/health");
        Assert.Equal("Disabled", health.GetProperty("status").GetString());
        var targets = await GetJson(client, "/api/v1/targets");
        Assert.Equal(3, targets.GetArrayLength());
        var transitions = await GetJson(client, "/api/v1/transitions?limit=500");
        Assert.True(transitions.GetProperty("items").GetArrayLength() > 0);
        var settings = await GetJson(client, "/api/v1/settings"); // gate open
        Assert.Equal("broker-secret", settings.GetProperty("Mqtt.Password").GetString());
        Assert.Equal("http://source.test/", settings.GetProperty("Clone.SourceUrl").GetString());
        Assert.Equal("false", settings.GetProperty("Engine.Enabled").GetString());

        // The source is untouched and still monitoring.
        Assert.Equal("Healthy",
            (await GetJson(_sourceClient, "/api/v1/health")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Takeover_disables_source_final_syncs_and_goes_live()
    {
        await SeedSourceAsync();
        var target = MakeTarget();
        var client = target.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        })).EnsureSuccessStatusCode();

        // The world moves on after the clone: the source records an outage.
        _source.Pings.SetUp(IPAddress.Parse("8.8.8.8"), false);
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await GetJson(_sourceClient, "/api/v1/status");
            foreach (var t in status.GetProperty("targets").EnumerateArray())
                if (t.GetProperty("name").GetString() == "Google")
                    return t.GetProperty("state").GetString() == "Down";
            return false;
        });

        var takeover = await client.PostAsJsonAsync("/api/v1/setup/takeover", new { allowInsecure = true });
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);

        // Source: disabled archive. Target: live, with the post-clone outage.
        Assert.Equal("Disabled",
            (await GetJson(_sourceClient, "/api/v1/health")).GetProperty("status").GetString());
        await ConfigApiTests.Eventually(async () =>
            (await GetJson(client, "/api/v1/health")).GetProperty("status").GetString() == "Healthy");
        var transitions = await GetJson(client, "/api/v1/transitions?limit=500");
        Assert.Contains(transitions.GetProperty("items").EnumerateArray(), i =>
            i.GetProperty("subjectName").GetString() == "Google" &&
            i.GetProperty("to").GetString() == "Down");
    }

    [Fact]
    public async Task Takeover_with_wrong_source_password_changes_nothing()
    {
        await SeedSourceAsync();
        var target = MakeTarget();
        var client = target.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        })).EnsureSuccessStatusCode();

        (await _sourceClient.PostAsJsonAsync("/api/v1/auth/set-password",
            new { password = "hunter2" })).EnsureSuccessStatusCode();

        var takeover = await client.PostAsJsonAsync("/api/v1/setup/takeover",
            new { sourcePassword = "nope", allowInsecure = true });
        Assert.Equal(HttpStatusCode.Unauthorized, takeover.StatusCode);
        Assert.Contains("source-auth", await takeover.Content.ReadAsStringAsync());

        // Source still monitoring; target still a disabled archive.
        Assert.Equal("Healthy",
            (await GetJson(_sourceClient, "/api/v1/health")).GetProperty("status").GetString());
        Assert.Equal("Disabled",
            (await GetJson(client, "/api/v1/health")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Takeover_over_plain_http_to_a_non_loopback_source_requires_the_override()
    {
        // The clone may have been non-sensitive (no password, no secrets), so
        // the consent given then does not cover take-over, which always sends
        // the password and pulls secrets.
        var target = MakeTarget();
        var client = target.CreateClient();
        var settings = target.Services.GetRequiredService<ConPersistence.SettingsStore>();
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [ConPersistence.SettingKeys.CloneSourceUrl] = "http://10.0.0.5:8080/",
        });

        var takeover = await client.PostAsJsonAsync("/api/v1/setup/takeover", new { sourcePassword = "pw" });
        Assert.Equal(HttpStatusCode.BadRequest, takeover.StatusCode);
        Assert.Contains("insecure-transport", await takeover.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Both_active_after_clone_raises_health_warning()
    {
        await SeedSourceAsync();
        var target = MakeTarget();
        var client = target.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        })).EnsureSuccessStatusCode();

        // While the clone is a disabled archive: no warning.
        var quiet = await GetJson(client, "/api/v1/config/health");
        Assert.DoesNotContain(quiet.GetProperty("warnings").EnumerateArray(),
            w => w.GetProperty("code").GetString() == "clone-source-active");

        // User error: enabling the clone WITHOUT taking over.
        (await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "true" }))
            .EnsureSuccessStatusCode();
        await ConfigApiTests.Eventually(async () =>
        {
            var health = await GetJson(client, "/api/v1/config/health");
            foreach (var w in health.GetProperty("warnings").EnumerateArray())
                if (w.GetProperty("code").GetString() == "clone-source-active")
                    return true;
            return false;
        });
    }
}
