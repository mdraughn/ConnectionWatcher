using System.Net;
using System.Text.Json;
using ConPersistence;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// Query-string timestamps: no offset means UTC (never the server's local
/// zone), an explicit offset is honored, garbage is a 400.
/// </summary>
public class TimeQueryApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public TimeQueryApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedTransitionsAsync()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var id = Guid.NewGuid();
        db.Transitions.AddRange(
            new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 12, 1, 0, 0, DateTimeKind.Utc), SubjectId = id,
                SubjectName = "Early", FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
            },
            new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 12, 3, 0, 0, DateTimeKind.Utc), SubjectId = id,
                SubjectName = "Late", FromState = "Down", ToState = "Responding", Trigger = "ThresholdCrossed",
            });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> NamesFrom(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("subjectName").GetString()!).ToList();
    }

    [Fact]
    public async Task Offset_less_from_is_utc()
    {
        await SeedTransitionsAsync();
        var names = await NamesFrom("/api/v1/transitions?from=2026-09-12T02:00:00");
        Assert.Equal(["Late"], names);
    }

    [Fact]
    public async Task Explicit_offset_is_honored()
    {
        await SeedTransitionsAsync();
        // 04:00 at +02:00 is 02:00Z: only the later row qualifies.
        var names = await NamesFrom("/api/v1/transitions?from=" + Uri.EscapeDataString("2026-09-12T04:00:00+02:00"));
        Assert.Equal(["Late"], names);
    }

    [Fact]
    public async Task Garbage_is_a_400_with_an_error_message()
    {
        var response = await _client.GetAsync("/api/v1/glitches?from=yesterday");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'from'", await response.Content.ReadAsStringAsync());
    }
}
