using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence;
using ConPersistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Tests;

public class GlitchApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;
    private static readonly Guid TargetA = Guid.NewGuid();
    private static readonly Guid TargetB = Guid.NewGuid();

    public GlitchApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedAsync()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var t0 = DateTime.Parse("2026-09-04T03:34:00Z").ToUniversalTime();
        db.Glitches.AddRange(
            new GlitchRow { TargetId = TargetA, TargetName = "Google", Class = 0,
                StartUtc = t0, EndUtc = t0.AddSeconds(9), LostCount = 2, SampleCount = 8 },
            new GlitchRow { TargetId = TargetB, TargetName = "Quad9", Class = 0,
                StartUtc = t0.AddMinutes(1), EndUtc = t0.AddMinutes(1).AddSeconds(6), LostCount = 3, SampleCount = 9 },
            new GlitchRow { TargetId = Guid.Parse("6E7E12E7-0000-4000-8000-000000000001"),
                TargetName = "Internet", Class = 1,
                StartUtc = t0, EndUtc = t0.AddSeconds(70), Detail = "anchors: Google, Quad9; trunk clean (upstream)" });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Lists_newest_first_with_filters()
    {
        await SeedAsync();
        var all = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync("/api/v1/glitches"));
        Assert.Equal(3, all.GetProperty("items").GetArrayLength());
        Assert.Equal("Quad9", all.GetProperty("items")[0].GetProperty("targetName").GetString());

        var filtered = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync($"/api/v1/glitches?targetId={TargetA}"));
        var item = Assert.Single(filtered.GetProperty("items").EnumerateArray());
        Assert.Equal("Google", item.GetProperty("targetName").GetString());
        Assert.EndsWith("+00:00", item.GetProperty("startUtc").GetString()); // UTC-kinded

        var windowed = JsonSerializer.Deserialize<JsonElement>(
            await _client.GetStringAsync("/api/v1/glitches?from=2026-09-04T03:34:30Z&to=2026-09-04T04:00:00Z"));
        Assert.Equal(1, windowed.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Exports_csv_and_json()
    {
        await SeedAsync();
        var csv = await _client.GetAsync("/api/v1/export/glitches?format=csv");
        csv.EnsureSuccessStatusCode();
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("targetName", text.Split('\n')[0]);
        Assert.Contains("Google", text);

        var json = await _client.GetAsync("/api/v1/export/glitches?format=json");
        json.EnsureSuccessStatusCode();
        Assert.Equal(3, JsonSerializer.Deserialize<JsonElement>(
            await json.Content.ReadAsStringAsync()).GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.GetAsync("/api/v1/export/glitches?format=xml")).StatusCode);
    }

    [Fact]
    public async Task Glitch_json_bytes_are_pinned()
    {
        var target = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var internet = Guid.Parse("6E7E12E7-0000-4000-8000-000000000001");
        long first, second;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var a = new GlitchRow
            {
                // The API's JSON goes out with relaxed escaping ('<' raw): fine for an
                // application/json download; the HTML islands are pinned in PageSmokeTests.
                TargetId = target, TargetName = "Google <edge>", Class = 0,
                StartUtc = new DateTime(2026, 9, 18, 10, 0, 0, 500, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 9, 18, 10, 0, 9, DateTimeKind.Utc),
                LostCount = 2, SampleCount = 8,
            };
            var b = new GlitchRow
            {
                TargetId = internet, TargetName = "Internet", Class = 1,
                StartUtc = new DateTime(2026, 9, 18, 10, 1, 0, 250, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 9, 18, 10, 2, 10, DateTimeKind.Utc),
                Detail = "anchors: Google, Quad9; trunk clean (upstream)",
            };
            db.Glitches.AddRange(a, b);
            await db.SaveChangesAsync();
            (first, second) = (a.Id, b.Id);
        }

        var response = await _client.GetAsync("/api/v1/export/glitches?format=json");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            "[{\"id\":" + first + ",\"targetId\":\"" + target + "\",\"targetName\":\"Google <edge>\"," +
            "\"class\":0,\"startUtc\":\"2026-09-18T10:00:00.5Z\",\"endUtc\":\"2026-09-18T10:00:09Z\"," +
            "\"lostCount\":2,\"sampleCount\":8,\"detail\":null}," +
            "{\"id\":" + second + ",\"targetId\":\"" + internet.ToString() + "\",\"targetName\":\"Internet\"," +
            "\"class\":1,\"startUtc\":\"2026-09-18T10:01:00.25Z\",\"endUtc\":\"2026-09-18T10:02:10Z\"," +
            "\"lostCount\":0,\"sampleCount\":0,\"detail\":\"anchors: Google, Quad9; trunk clean (upstream)\"}]",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Glitch_reads_stay_open_when_admin_gate_is_on()
    {
        await SeedAsync();
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        await _client.PostAsync("/api/v1/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/glitches")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/v1/export/glitches?format=json")).StatusCode);
    }
}
