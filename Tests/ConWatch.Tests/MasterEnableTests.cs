using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>Master enable switch: archive mode, health semantics, snapshot
/// per-node exclusion, and the M2M control endpoint.</summary>
public class MasterEnableTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public MasterEnableTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Disable_parks_engine_serves_archive_and_reenable_recovers()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            var health = await GetJson("/api/v1/health");
            return health.GetProperty("status").GetString() == "Healthy";
        });

        var off = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        await ConfigApiTests.Eventually(async () =>
        {
            var health = await GetJson("/api/v1/health");
            return health.GetProperty("status").GetString() == "Disabled";
        });

        // Archive answers: health 200, status 200 with the archive shape,
        // history APIs still serving.
        var healthResponse = await _client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        var status = await _client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var statusBody = JsonSerializer.Deserialize<JsonElement>(
            await status.Content.ReadAsStringAsync());
        Assert.False(statusBody.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK,
            (await _client.GetAsync("/api/v1/transitions?limit=5")).StatusCode);

        var on = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "true" });
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        await ConfigApiTests.Eventually(async () =>
        {
            var health = await GetJson("/api/v1/health");
            return health.GetProperty("status").GetString() == "Healthy";
        });
    }

    private async Task DisableAndWaitForArchiveAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(() => Task.FromResult(host.Engine is not null));
        var off = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        await ConfigApiTests.Eventually(() => Task.FromResult(host.Engine is null && !host.EngineEnabled));
    }

    [Fact]
    public async Task Archive_mode_status_and_mcp_answers_are_unchanged()
    {
        await DisableAndWaitForArchiveAsync();

        var status = await _client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await status.Content.ReadAsStringAsync());
        Assert.False(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("targets").ValueKind);
        Assert.Equal(0, body.GetProperty("targets").GetArrayLength());

        using var scope = _factory.Services.CreateScope();
        var tools = ActivatorUtilities.CreateInstance<ConWatch.Mcp.ConWatchMcpTools>(scope.ServiceProvider);
        var mcp = JsonSerializer.Deserialize<JsonElement>(await tools.GetCurrentStatus());
        Assert.False(mcp.GetProperty("monitoringEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mcp.GetProperty("targets").ValueKind);
    }

    [Fact]
    public async Task Archive_dashboard_embeds_the_disabled_snapshot()
    {
        await DisableAndWaitForArchiveAsync();

        var response = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var match = System.Text.RegularExpressions.Regex.Match(await response.Content.ReadAsStringAsync(),
            """<script id="initial-data" type="application/json">(.*?)</script>""",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(match.Success, "The Index page has no initial-data island.");
        var island = JsonSerializer.Deserialize<JsonElement>(match.Groups[1].Value);
        var snapshot = island.GetProperty("snapshot");
        Assert.Equal(JsonValueKind.Object, snapshot.ValueKind);
        Assert.False(snapshot.GetProperty("enabled").GetBoolean());
        Assert.True(island.GetProperty("engineDisabled").GetBoolean());
    }

    [Fact]
    public async Task Settings_toggle_is_reflected_by_health_before_the_rebuild_lands()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
            (await GetJson("/api/v1/health")).GetProperty("status").GetString() == "Healthy");

        var off = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        // The cached switch flips on the settings event, not on the rebuild:
        // a reload right after the PUT must already say enabled=false.
        var health = await GetJson("/api/v1/health");
        Assert.False(health.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Snapshot_never_carries_per_node_keys()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var put = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var snapshot = await GetJson("/api/v1/export/snapshot");
        var settings = snapshot.GetProperty("settings");
        Assert.False(settings.TryGetProperty("Engine.Enabled", out _));
        Assert.False(settings.TryGetProperty("Clone.SourceUrl", out _));
    }

    [Fact]
    public async Task M2M_engine_endpoint_toggles_and_verifies_password_inline()
    {
        await ConfigApiTests.SeedHomeAsync(_client);

        // Gate open: no password needed; effective when the call returns.
        var off = await _client.PostAsJsonAsync("/api/v1/admin/engine",
            new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        var health = await GetJson("/api/v1/health");
        Assert.Equal("Disabled", health.GetProperty("status").GetString());

        // Gate set: wrong password 401, right password flips it back.
        var set = await _client.PostAsJsonAsync("/api/v1/auth/set-password",
            new { password = "hunter2" });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);
        var anon = _factory.CreateClient(); // no admin cookie

        var wrong = await anon.PostAsJsonAsync("/api/v1/admin/engine",
            new { enabled = true, password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var right = await anon.PostAsJsonAsync("/api/v1/admin/engine",
            new { enabled = true, password = "hunter2" });
        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        await ConfigApiTests.Eventually(async () =>
            (await GetJson("/api/v1/health")).GetProperty("status").GetString() == "Healthy");
    }

    private static Task<int> SettledRebuildCountAsync(WatcherHostService host) =>
        HostTestWait.SettledRebuildCountAsync(host);

    [Fact]
    public async Task M2M_engine_toggle_rebuilds_exactly_once()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(async () =>
            (await GetJson("/api/v1/health")).GetProperty("status").GetString() == "Healthy");
        var before = await SettledRebuildCountAsync(host);

        var off = await _client.PostAsJsonAsync("/api/v1/admin/engine", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        await ConfigApiTests.Eventually(() => Task.FromResult(host.RebuildCount >= before + 1));

        // A second, debounced rebuild would land in the settled window.
        Assert.Equal(before + 1, await SettledRebuildCountAsync(host));
    }

    [Fact]
    public async Task Unconditional_rebuild_runs_without_a_preceding_write_but_the_stale_check_skips()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(async () =>
            (await GetJson("/api/v1/health")).GetProperty("status").GetString() == "Healthy");
        var before = await SettledRebuildCountAsync(host);

        Assert.True(await host.RebuildIfStaleAsync()); // nothing changed since the last rebuild: no-op
        Assert.Equal(before, host.RebuildCount);

        Assert.True(await host.RebuildAsync());        // unconditional: runs
        Assert.Equal(before + 1, host.RebuildCount);
    }
}
