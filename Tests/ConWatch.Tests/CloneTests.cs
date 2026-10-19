using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConEngine;
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

    private ConWatchFactory MakeTarget(Action<IServiceCollection>? extra = null)
    {
        _target = new ConWatchFactory
        {
            TestServices = services =>
            {
                services.AddHttpClient(CloneService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _source.Server.CreateHandler());
                extra?.Invoke(services);
            },
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

    private async Task SeedSourceWithLocalNasAsync()
    {
        await SeedSourceAsync();
        (await _sourceClient.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        // The Local target must have recorded history to be carried (or skipped).
        await ConfigApiTests.Eventually(async () =>
            (await GetJson(_sourceClient, "/api/v1/transitions?scope=local&limit=10"))
                .GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Clone_copies_Local_devices_and_their_history_by_default()
    {
        await SeedSourceWithLocalNasAsync();
        var target = MakeTarget();
        var client = target.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var targets = await GetJson(client, "/api/v1/targets");
        var nas = targets.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "NasBox");
        Assert.Equal("Local", nas.GetProperty("scope").GetString());
        Assert.True((await GetJson(client, "/api/v1/transitions?scope=local&limit=10"))
            .GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Clone_without_Local_devices_leaves_no_trace_of_them_and_keeps_everything_else()
    {
        await SeedSourceWithLocalNasAsync();
        var target = MakeTarget();
        var client = target.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
            includeLocalDevices = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, summary.GetProperty("localDevicesSkipped").GetInt32());

        var names = (await GetJson(client, "/api/v1/targets")).EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("NasBox", names);
        Assert.Contains("Router", names);                       // verdict-scope targets still cloned
        var pools = await GetJson(client, "/api/v1/pools");
        Assert.Equal(1, pools.GetArrayLength());                 // pools untouched
        // No orphan history for the skipped device: nothing in the log names it.
        var all = (await GetJson(client, "/api/v1/transitions?limit=1000")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(all, i => i.GetProperty("subjectName").GetString() == "NasBox");
        var glitches = (await GetJson(client, "/api/v1/glitches?limit=1000")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(glitches, g => g.GetProperty("targetName").GetString() == "NasBox");
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
    public async Task Takeover_after_a_clone_without_Local_devices_does_not_bring_them_back()
    {
        await SeedSourceWithLocalNasAsync();
        var target = MakeTarget();
        var client = target.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
            includeLocalDevices = false,
        })).EnsureSuccessStatusCode();

        var takeover = await client.PostAsJsonAsync("/api/v1/setup/takeover", new { allowInsecure = true });
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);
        var summary = JsonSerializer.Deserialize<JsonElement>(await takeover.Content.ReadAsStringAsync());
        Assert.Equal(1, summary.GetProperty("localDevicesSkipped").GetInt32());

        var names = (await GetJson(client, "/api/v1/targets")).EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("NasBox", names);
        Assert.Contains("Router", names);
        var all = (await GetJson(client, "/api/v1/transitions?limit=1000")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(all, i => i.GetProperty("subjectName").GetString() == "NasBox");
    }

    [Fact]
    public async Task Takeover_after_a_default_clone_keeps_Local_devices()
    {
        await SeedSourceWithLocalNasAsync();
        var target = MakeTarget();
        var client = target.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        })).EnsureSuccessStatusCode();

        var takeover = await client.PostAsJsonAsync("/api/v1/setup/takeover", new { allowInsecure = true });
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);

        var nas = (await GetJson(client, "/api/v1/targets")).EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "NasBox");
        Assert.Equal("Local", nas.GetProperty("scope").GetString());
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
        var settings = target.Services.GetRequiredService<ConPersistence.Settings.SettingsStore>();
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [ConPersistence.Settings.SettingKeys.CloneSourceUrl] = "http://10.0.0.5:8080/",
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

    /// <summary>
    /// The import has landed but the archive rebuild fails: the clone must say
    /// so (500, code rebuild-failed), not report success on a node that is
    /// not in the state the caller was told.
    /// </summary>
    [Fact]
    public async Task Clone_import_with_a_failing_rebuild_answers_500()
    {
        await SeedSourceAsync();
        var bomb = new DetachBombObserver();
        var target = MakeTarget(s => s.AddSingleton<IEngineObserver>(bomb));
        var client = target.CreateClient();
        // Long debounce: the import's own change events must not rebuild (and
        // eat the one-shot failure) before the clone's direct rebuild runs.
        HostTestWait.SetRebuildDebounce(target.Services, 60);
        bomb.Arm();

        var response = await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("rebuild-failed", body.GetProperty("code").GetString());
        Assert.Contains("archive", body.GetProperty("errors")[0].GetString());
        Assert.True(bomb.Fired);
    }
}
