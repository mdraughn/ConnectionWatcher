using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence.Config;

namespace ConWatch.Tests;

/// <summary>The Critical flag through the REST API, config export/import and snapshots.</summary>
public class CriticalFieldTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public CriticalFieldTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<ConfigDocument> ExportAsync() =>
        JsonSerializer.Deserialize<ConfigDocument>(
            await _client.GetStringAsync("/api/v1/config/export"), Web)!;

    [Fact]
    public async Task Critical_round_trips_through_create_update_export_and_import()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Router", address = "192.168.1.1", profile = "Internal", critical = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Google", address = "8.8.8.8", parent = "Router", profile = "Public",
        })).EnsureSuccessStatusCode();

        var doc = await ExportAsync();
        var router = doc.Targets.Single(t => t.Name == "Router");
        Assert.True(router.Critical);
        Assert.False(doc.Targets.Single(t => t.Name == "Google").Critical); // no key = false

        var update = await _client.PutAsJsonAsync($"/api/v1/targets/{router.Id}",
            router with { Critical = false });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.False((await ExportAsync()).Targets.Single(t => t.Name == "Router").Critical);

        // Import carries it back.
        var import = await _client.PostAsJsonAsync("/api/v1/config/import", doc);
        Assert.True(import.IsSuccessStatusCode, await import.Content.ReadAsStringAsync());
        Assert.True((await ExportAsync()).Targets.Single(t => t.Name == "Router").Critical);

        // And a snapshot carries it.
        var snapshot = await _client.GetStringAsync("/api/v1/export/snapshot");
        Assert.Contains("\"critical\":true", snapshot);
    }
}
