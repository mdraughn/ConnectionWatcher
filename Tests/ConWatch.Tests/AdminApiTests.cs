using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConEngine;
using ConPersistence;
using ConPersistence.Recording;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class AdminApiTests : IDisposable
{
    private readonly DetachBombObserver _detachBomb = new();
    private readonly ConWatchFactory _factory;
    private readonly HttpClient _client;

    public AdminApiTests()
    {
        _factory = new ConWatchFactory { TestServices = s => s.AddSingleton<IEngineObserver>(_detachBomb) };
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> Stats()
    {
        var response = await _client.GetAsync("/api/v1/admin/stats");
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stats_timestamps_carry_a_utc_offset()
    {
        // SQLite hands DateTimes back Kind=Unspecified; serialized bare they
        // get parsed as LOCAL by the browser and display shifted. Every
        // timestamp the API emits must carry Z or +00:00.
        await ConfigApiTests.SeedHomeAsync(_client);
        string? oldest = null;
        await ConfigApiTests.Eventually(async () =>
        {
            await _factory.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
            var stats = await Stats();
            var el = stats.GetProperty("oldestTransitionUtc");
            oldest = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            return oldest is not null;
        });
        Assert.Matches(@"(Z|\+00:00)$", oldest!);
    }

    [Fact]
    public async Task Stats_reflect_seeded_data()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var stats = await Stats();
        Assert.True(stats.GetProperty("fileSizeBytes").GetInt64() > 0);
        Assert.Equal(3, stats.GetProperty("tables").GetProperty("targets").GetInt32());
        Assert.Equal(1, stats.GetProperty("tables").GetProperty("pools").GetInt32());
        Assert.Equal(6, stats.GetProperty("tables").GetProperty("profiles").GetInt32()); // 5 builtins + Fast
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

        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.ModelWarnings.Add(new ConPersistence.Entities.ModelWarningRow
                { Key = "unprovable-up", Kind = "UnprovableUp", Message = "m", StartUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var wrong = await _client.PostAsJsonAsync("/api/v1/admin/clear-history", new { confirm = "clear" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var ok = await _client.PostAsJsonAsync("/api/v1/admin/clear-history", new { confirm = "CLEAR" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        await using (var db = await dbFactory.CreateDbContextAsync())
            Assert.Empty(db.ModelWarnings);

        var afterItems = (await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            "/api/v1/transitions?limit=500")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(afterItems, i => i.GetProperty("id").GetInt64() <= lastBefore);

        var after = await Stats();
        // Rollups: at most the freshly-flushed current minute since the clear.
        Assert.True(after.GetProperty("tables").GetProperty("rollups").GetInt32() <= 1);
        Assert.Equal(3, after.GetProperty("tables").GetProperty("targets").GetInt32());
    }

    [Fact]
    public async Task Prune_removes_only_rows_before_the_date_and_reports_counts()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            await _factory.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
            var stats = await Stats();
            return stats.GetProperty("tables").GetProperty("transitions").GetInt32() > 0;
        });

        var wrong = await _client.PostAsJsonAsync("/api/v1/admin/prune",
            new { beforeUtc = "2026-01-01T00:00:00Z", confirm = "prune" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var noDate = await _client.PostAsJsonAsync("/api/v1/admin/prune", new { confirm = "PRUNE" });
        Assert.Equal(HttpStatusCode.BadRequest, noDate.StatusCode);

        // Cutoff in the past: recent rows and config are untouched.
        var past = await _client.PostAsJsonAsync("/api/v1/admin/prune",
            new { beforeUtc = DateTime.UtcNow.AddYears(-1).ToString("o"), confirm = "PRUNE" });
        Assert.Equal(HttpStatusCode.OK, past.StatusCode);
        var before = await Stats();
        Assert.True(before.GetProperty("tables").GetProperty("transitions").GetInt32() > 0);

        // Cutoff in the future: everything recorded so far goes except each
        // subject's latest row; config survives.
        var future = await _client.PostAsJsonAsync("/api/v1/admin/prune",
            new { beforeUtc = DateTime.UtcNow.AddDays(1).ToString("o"), confirm = "PRUNE" });
        Assert.Equal(HttpStatusCode.OK, future.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await future.Content.ReadAsStringAsync());
        Assert.True(body.GetProperty("transitions").GetInt32() >= 0);

        var after = await Stats();
        Assert.Equal(3, after.GetProperty("tables").GetProperty("targets").GetInt32());
        // Latest-per-subject guard: at least the subjects' newest rows remain.
        var transitions = await _client.GetFromJsonAsync<JsonElement>("/api/v1/transitions?limit=500");
        Assert.True(transitions.GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Factory_reset_wipes_everything_and_reseeds_builtins()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[] { new { address = "192.0.2.50", hostname = (string?)null, kind = "LocalDevice",
                method = "ArpTable", origin = "tray:PC", evidence = "e" } },
        })).EnsureSuccessStatusCode();

        var ok = await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var inbox = await _client.GetFromJsonAsync<JsonElement>("/api/v1/discoveries?status=all");
        Assert.Equal(0, inbox.GetArrayLength());
        var targets = await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets");
        Assert.Empty(targets!);
        var profiles = await _client.GetFromJsonAsync<List<ConPersistence.Config.ProfileDto>>("/api/v1/profiles");
        Assert.Equal(5, profiles!.Count); // builtins only
        var settings = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings");
        Assert.Equal("2", settings!["Verdict.WeatherThreshold"]); // defaults re-seeded
    }

    [Fact]
    public async Task Factory_reset_restores_the_first_boot_settings()
    {
        string[] keys =
        [
            SettingKeys.WeatherThreshold, SettingKeys.MinuteRetentionDays, SettingKeys.HourlyRetentionMonths,
            SettingKeys.TransitionRetentionMonths, SettingKeys.RetentionRunHourUtc,
            SettingKeys.WarmRestartMaxGapMinutes, SettingKeys.AnchorPool,
        ];
        var firstBoot = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings");

        var reset = await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var after = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings");
        foreach (var key in keys)
            Assert.True(after!.TryGetValue(key, out var value) && value == firstBoot![key],
                $"{key}: first boot '{firstBoot![key]}', after reset '{value}'");
    }

    /// <summary>A running node with a real engine, and a long debounce so only the endpoint's own rebuild runs.</summary>
    private async Task<WatcherHostService> RunningNodeAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await HostTestWait.Eventually(() => Task.FromResult(host.EngineRunning));
        await HostTestWait.SettledRebuildCountAsync(host);
        // A debounced rebuild racing the endpoint's own would eat the one-shot
        // failure and let the endpoint's rebuild succeed.
        HostTestWait.SetRebuildDebounce(_factory.Services, 60);
        return host;
    }

    private static async Task AssertRebuildFailedBody(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(["errors"], body.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Contains("rebuilt", body.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task A_failed_archive_rebuild_answers_500()
    {
        await RunningNodeAsync();
        _detachBomb.Arm();

        // Engine.Enabled=false takes the archive path, which detaches the observers first.
        await AssertRebuildFailedBody(await _client.PostAsJsonAsync("/api/v1/admin/engine", new { enabled = false }));
        Assert.True(_detachBomb.Fired);
    }

    [Fact]
    public async Task A_failed_rebuild_after_factory_reset_answers_500()
    {
        await RunningNodeAsync();
        _detachBomb.Arm();

        await AssertRebuildFailedBody(
            await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" }));
        Assert.True(_detachBomb.Fired);
    }
}
