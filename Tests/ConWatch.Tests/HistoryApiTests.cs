using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class HistoryApiTests : IDisposable
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public HistoryApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private async Task<string?> StateOf(string name)
    {
        var response = await _client.GetAsync("/api/v1/status");
        if (!response.IsSuccessStatusCode)
            return null;
        var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        foreach (var t in status.GetProperty("targets").EnumerateArray())
            if (t.GetProperty("name").GetString() == name)
                return t.GetProperty("state").GetString();
        return null;
    }

    [Fact]
    public async Task Outage_produces_queryable_transitions_and_rollups()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () => await StateOf("Google") == "Responding");

        // Outage: router down -> descendants unreachable; then restore.
        // A real outage: the router and everything beyond it stop answering.
        _factory.Pings.SetUp(RouterIp, false);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("8.8.8.8"), false);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("1.1.1.1"), false);
        await ConfigApiTests.Eventually(async () => await StateOf("Router") == "Down");
        await ConfigApiTests.Eventually(async () => await StateOf("Google") == "Unreachable");
        _factory.Pings.SetUp(RouterIp, true);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("8.8.8.8"), true);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("1.1.1.1"), true);
        await ConfigApiTests.Eventually(async () => await StateOf("Router") == "Responding");

        // Deterministic persistence: drain the recorder and flush open rollup buckets.
        await _factory.Services.GetRequiredService<TransitionRecorder>().FlushAsync();
        await _factory.Services.GetRequiredService<RollupCollector>().FlushAllAsync();

        var transitions = await GetJson("/api/v1/transitions?limit=500");
        var items = transitions.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.Contains(items, i =>
            i.GetProperty("subjectName").GetString() == "Google" &&
            i.GetProperty("trigger").GetString() == "ParentDown");
        Assert.Contains(items, i =>
            i.GetProperty("subjectKind").GetString() == "Verdict" &&
            i.GetProperty("verdict").GetString() is not null);

        // Rollups for Router exist at minute resolution.
        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        var routerId = targets!.Single(t => t.Name == "Router").Id;
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddHours(1).ToString("o"));
        var rollups = await GetJson($"/api/v1/rollups/{routerId}?from={from}&to={to}&resolution=minute");
        Assert.Equal("Minute", rollups.GetProperty("resolution").GetString());
        Assert.True(rollups.GetProperty("points").GetArrayLength() >= 1);
        var point = rollups.GetProperty("points")[0];
        Assert.True(point.GetProperty("pingCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Auto_resolution_follows_the_settings_retention()
    {
        // The Settings page edits the Settings table, and retention applies that, not
        // appsettings: with minute rows kept 7 days, a 30-day chart must read hour rows.
        var settings = _factory.Services.GetRequiredService<ConPersistence.Settings.SettingsStore>();
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [ConPersistence.Settings.SettingKeys.MinuteRetentionDays] = "7",
        });
        var targetId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            for (int day = 8; day <= 30; day++)
                db.Rollups.Add(new RollupRow
                {
                    TargetId = targetId, PeriodStartUtc = now.AddDays(-day), Resolution = RollupResolution.Hour,
                    PingCount = 3600, SuccessCount = 3600, RttAvg = 10, RttMin = 5, RttMax = 20,
                });
            for (int day = 1; day <= 6; day++)
                db.Rollups.Add(new RollupRow
                {
                    TargetId = targetId, PeriodStartUtc = now.AddDays(-day), Resolution = RollupResolution.Minute,
                    PingCount = 60, SuccessCount = 60, RttAvg = 10, RttMin = 5, RttMax = 20,
                });
            await db.SaveChangesAsync();
        }

        var from = Uri.EscapeDataString(now.AddDays(-30).AddHours(-1).ToString("o"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("o"));
        var auto = await GetJson($"/api/v1/rollups/{targetId}?from={from}&to={to}&resolution=auto");
        Assert.Equal("Hour", auto.GetProperty("resolution").GetString());
        Assert.Equal(23, auto.GetProperty("points").GetArrayLength());

        // A 3-day chart is still inside the 7-day minute window.
        var recent = Uri.EscapeDataString(now.AddDays(-3).AddHours(-1).ToString("o"));
        var near = await GetJson($"/api/v1/rollups/{targetId}?from={recent}&to={to}&resolution=auto");
        Assert.Equal("Minute", near.GetProperty("resolution").GetString());
    }

    [Theory]
    [InlineData("hour", "Hour")]
    [InlineData("day", "Day")]
    [InlineData("minute", "Minute")]
    public async Task Explicit_resolution_passes_through(string requested, string expected)
    {
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-30).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddHours(1).ToString("o"));
        var body = await GetJson($"/api/v1/rollups/{Guid.NewGuid()}?from={from}&to={to}&resolution={requested}");
        Assert.Equal(expected, body.GetProperty("resolution").GetString());
    }

    [Fact]
    public async Task Bogus_resolution_is_a_400()
    {
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddHours(1).ToString("o"));
        var response = await _client.GetAsync($"/api/v1/rollups/{Guid.NewGuid()}?from={from}&to={to}&resolution=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rollups_require_time_range()
    {
        var response = await _client.GetAsync($"/api/v1/rollups/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Transitions_filter_by_subject_name()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () => await StateOf("Google") == "Responding");
        var recorder = _factory.Services.GetRequiredService<TransitionRecorder>();
        await ConfigApiTests.Eventually(async () =>
        {
            await recorder.FlushAsync();
            var p = await GetJson("/api/v1/transitions?subjectName=Internet&limit=100");
            return p.GetProperty("items").GetArrayLength() > 0;
        });

        // The dashboard's verdict clock leans on this filter: only rows for
        // the named subject (the verdict AND the same-named pool), nothing else.
        var page = await GetJson("/api/v1/transitions?subjectName=Internet&limit=100");
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, i => Assert.Equal("Internet", i.GetProperty("subjectName").GetString()));
        Assert.Contains(items, i => i.GetProperty("verdict").GetString() is not null);
    }

    [Fact]
    public async Task Transitions_paging_returns_cursor()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () => await StateOf("Google") == "Responding");

        // Recording is asynchronous (background loop + our flush can race on
        // the channel), so poll for the precondition rather than assuming one
        // flush settles the world.
        var recorder = _factory.Services.GetRequiredService<TransitionRecorder>();
        await ConfigApiTests.Eventually(async () =>
        {
            await recorder.FlushAsync();
            var p = await GetJson("/api/v1/transitions?limit=2");
            return p.GetProperty("items").GetArrayLength() == 2;
        });

        var page = await GetJson("/api/v1/transitions?limit=2");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("nextBefore").ValueKind == JsonValueKind.Number);

        var before = page.GetProperty("nextBefore").GetInt64();
        var page2 = await GetJson($"/api/v1/transitions?limit=2&before={before}");
        var ids1 = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64());
        var ids2 = page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt64());
        Assert.Empty(ids1.Intersect(ids2));
    }

    [Fact]
    public async Task Transitions_endpoint_wire_shape_is_pinned()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        long id;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var row = new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 18, 10, 0, 0, 123, DateTimeKind.Utc),
                SubjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SubjectName = "Router", SubjectKind = "Target",
                FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
                SuccessRate = 0.1, SampleSize = 10, AvgRtt = 12.5f,
            };
            db.Transitions.Add(row);
            await db.SaveChangesAsync();
            id = row.Id;
        }

        var body = await GetJson("/api/v1/transitions?limit=1");

        Assert.Equal(
            $$"""{"id":{{id}},"timestamp":"2026-09-18T10:00:00.123+00:00","subjectId":"11111111-1111-1111-1111-111111111111","subjectName":"Router","subjectKind":"Target","from":"Responding","to":"Down","trigger":"ThresholdCrossed","successRate":0.1,"sampleSize":10,"burstLossRate":null,"ancestorId":null,"detail":null,"verdict":null,"avgRtt":12.5,"scope":"Unknown"}""",
            body.GetProperty("items")[0].GetRawText());
    }
}
