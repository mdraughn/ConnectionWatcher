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
}
