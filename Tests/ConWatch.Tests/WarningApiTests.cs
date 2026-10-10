using System.Net;
using System.Text.Json;
using ConPersistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class WarningApiTests : IDisposable
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public WarningApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    // The engine answers 503 for a moment after the seed restarts it; wait that out.
    private async Task<JsonElement> StatusAsync()
    {
        HttpResponseMessage response = null!;
        await ConfigApiTests.Eventually(async () =>
            (response = await _client.GetAsync("/api/v1/status")).IsSuccessStatusCode);
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static JsonElement[] Warnings(JsonElement status) =>
        status.GetProperty("warnings").EnumerateArray().ToArray();

    [Fact]
    public async Task A_bypassed_router_is_a_note_that_acknowledges_and_clears()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
            (await StatusAsync()).GetProperty("verdict") is { ValueKind: JsonValueKind.Object } v
            && v.GetProperty("state").GetString() == "Healthy");

        // Router stops answering its probe; Google and Cloudflare behind it still answer.
        _factory.Pings.SetUp(RouterIp, false);
        JsonElement note = default;
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await StatusAsync();
            note = Warnings(status).FirstOrDefault(w => w.GetProperty("kind").GetString() == "HopBypassed");
            return note.ValueKind == JsonValueKind.Object;
        });
        Assert.Equal("Router", note.GetProperty("targetName").GetString());
        Assert.False(note.GetProperty("acknowledged").GetBoolean());
        Assert.Equal("Healthy", (await StatusAsync()).GetProperty("verdict").GetProperty("state").GetString());

        var key = note.GetProperty("key").GetString()!;
        var ack = await _client.PostAsync($"/api/v1/warnings/{Uri.EscapeDataString(key)}/acknowledge", null);
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        Assert.True(Warnings(await StatusAsync()).Single(w => w.GetProperty("key").GetString() == key)
            .GetProperty("acknowledged").GetBoolean());

        var missing = await _client.PostAsync("/api/v1/warnings/hop-bypassed%3Anope/acknowledge", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var metrics = await _client.GetStringAsync("/metrics");
        Assert.Contains("conwatch_model_warnings 1", metrics);

        _factory.Pings.SetUp(RouterIp, true);
        await ConfigApiTests.Eventually(async () => Warnings(await StatusAsync()).Length == 0);
        await ConfigApiTests.Eventually(async () =>
        {
            await using var db = await _factory.Services
                .GetRequiredService<IDbContextFactory<ConWatchDbContext>>().CreateDbContextAsync();
            return await db.ModelWarnings.AnyAsync(w => w.Key == key && w.EndUtc != null && w.AcknowledgedUtc != null);
        });
    }
}
