using System.Net;
using System.Text.Json;
using ConPersistence;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class ExportApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ExportApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedRows(int transitionCount)
    {
        using var scope = _factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var subject = Guid.NewGuid();
        for (int i = 0; i < transitionCount; i++)
        {
            db.Transitions.Add(new TransitionRow
            {
                TimestampUtc = DateTime.UtcNow.AddMinutes(-transitionCount + i),
                SubjectId = subject, SubjectName = $"T{i % 3}", SubjectKind = "Target",
                FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
                Detail = i == 0 ? "has, comma" : null,
            });
        }
        db.Rollups.Add(new RollupRow
        {
            TargetId = subject, PeriodStartUtc = DateTime.UtcNow.Date, Resolution = "Minute",
            PingCount = 60, SuccessCount = 59, RttAvg = 12, RttMin = 8, RttMax = 30,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Csv_export_returns_all_rows_past_the_query_api_cap()
    {
        await SeedRows(1500);
        var response = await _client.GetAsync("/api/v1/export/transitions?format=csv");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.TrimEnd('\n').Split('\n');
        // Header + 1500 rows (a row with an embedded newline would break this
        // count, so the seed keeps Detail single-line except the comma case).
        Assert.Equal(1501, lines.Length);
        Assert.StartsWith("id,timestampUtc", lines[0]);
        Assert.Contains("\"has, comma\"", body);
    }

    [Fact]
    public async Task Json_export_parses_and_filters_by_time()
    {
        await SeedRows(10);
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-5).ToString("o"));
        var response = await _client.GetAsync($"/api/v1/export/transitions?format=json&from={from}");
        response.EnsureSuccessStatusCode();
        var rows = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.True(rows.GetArrayLength() is > 0 and < 10);
    }

    [Fact]
    public async Task Rollup_export_works_in_both_formats()
    {
        await SeedRows(1);
        var csv = await _client.GetAsync("/api/v1/export/rollups?format=csv");
        csv.EnsureSuccessStatusCode();
        Assert.Contains("targetId,periodStartUtc", await csv.Content.ReadAsStringAsync());

        var json = await _client.GetAsync("/api/v1/export/rollups?format=json");
        json.EnsureSuccessStatusCode();
        var rows = JsonSerializer.Deserialize<JsonElement>(await json.Content.ReadAsStringAsync());
        Assert.Equal(1, rows.GetArrayLength());
    }

    [Fact]
    public async Task Bad_format_is_400()
    {
        var response = await _client.GetAsync("/api/v1/export/transitions?format=xml");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Json_export_timestamps_carry_the_utc_marker()
    {
        await SeedRows(1);
        var body = await (await _client.GetAsync("/api/v1/export/transitions?format=json")).Content.ReadAsStringAsync();
        Assert.Matches("\"timestampUtc\":\"[^\"]+Z\"", body);
        var snapshot = await (await _client.GetAsync("/api/v1/export/snapshot")).Content.ReadAsStringAsync();
        Assert.Matches("\"periodStartUtc\":\"[^\"]+Z\"", snapshot);
    }

    private const string TransitionHeader =
        "id,timestampUtc,subjectId,subjectName,subjectKind,from,to,trigger," +
        "successRate,sampleSize,burstLossRate,ancestorId,detail,verdict,avgRtt";

    [Fact]
    public async Task Transition_csv_bytes_and_download_name_are_pinned()
    {
        var subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
        long first, second;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var a = new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 18, 10, 0, 0, 123, DateTimeKind.Utc),
                SubjectId = subject, SubjectName = "Router", SubjectKind = "Target",
                FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
                SuccessRate = 0.1, SampleSize = 10, AvgRtt = 12.5f, Detail = "has, comma",
            };
            var b = new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 18, 10, 5, 0, 250, DateTimeKind.Utc),
                SubjectId = subject, SubjectName = "Router", SubjectKind = "Target",
                FromState = "Down", ToState = "Responding", Trigger = "ThresholdCrossed",
            };
            db.Transitions.AddRange(a, b);
            await db.SaveChangesAsync();
            (first, second) = (a.Id, b.Id);
        }

        var response = await _client.GetAsync("/api/v1/export/transitions?format=csv");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Matches(@"^conwatch-transitions-\d{8}-\d{6}\.csv$",
            disposition.FileNameStar ?? disposition.FileName!.Trim('"'));
        Assert.Equal(
            TransitionHeader + "\n" +
            $"{first},2026-09-18T10:00:00.123Z,{subject},Router,Target,Responding,Down,ThresholdCrossed,0.1,10,,,\"has, comma\",,12.5\n" +
            $"{second},2026-09-18T10:05:00.25Z,{subject},Router,Target,Down,Responding,ThresholdCrossed,,,,,,,0\n",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Empty_exports_are_a_header_line_or_an_empty_array()
    {
        Assert.Equal(TransitionHeader + "\n",
            await _client.GetStringAsync("/api/v1/export/transitions?format=csv"));
        Assert.Equal("targetId,periodStartUtc,resolution,pingCount,successCount,rttAvg,rttMin,rttMax\n",
            await _client.GetStringAsync("/api/v1/export/rollups?format=csv"));
        Assert.Equal("id,targetId,targetName,class,startUtc,endUtc,lostCount,sampleCount,detail\n",
            await _client.GetStringAsync("/api/v1/export/glitches?format=csv"));
        Assert.Equal("[]", await _client.GetStringAsync("/api/v1/export/transitions?format=json"));
        Assert.Equal("[]", await _client.GetStringAsync("/api/v1/export/rollups?format=json"));
        Assert.Equal("[]", await _client.GetStringAsync("/api/v1/export/glitches?format=json"));
    }

    [Fact]
    public async Task Glitch_export_works_in_both_formats()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Glitches.Add(new GlitchRow
            {
                TargetId = Guid.NewGuid(), TargetName = "Router", Class = 1,
                StartUtc = DateTime.UtcNow.AddMinutes(-2), EndUtc = DateTime.UtcNow.AddMinutes(-1),
                LostCount = 2, SampleCount = 10,
            });
            await db.SaveChangesAsync();
        }

        var csv = (await _client.GetStringAsync("/api/v1/export/glitches?format=csv")).TrimEnd('\n').Split('\n');
        Assert.Equal(2, csv.Length);
        Assert.Contains(",Router,Internet,", csv[1]);
        var json = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/export/glitches?format=json"));
        Assert.Equal(1, json.GetArrayLength());
    }
}
