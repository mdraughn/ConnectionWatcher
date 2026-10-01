using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence.Config;
using ConPersistence.Snapshots;

namespace ConWatch.Tests;

/// <summary>Probe override on targets: null/absent = ICMP, "tcp:&lt;port&gt;" = TCP connect.</summary>
public class ProbeFieldTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ProbeFieldTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<ConfigDocument> ExportAsync() =>
        JsonSerializer.Deserialize<ConfigDocument>(
            await _client.GetStringAsync("/api/v1/config/export"), Web)!;

    [Fact]
    public async Task Probe_survives_create_update_and_export()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Modem", address = "10.1.10.1", profile = "Public", probe = "tcp:80",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var exported = Assert.Single((await ExportAsync()).Targets, t => t.Name == "Modem");
        Assert.Equal("tcp:80", exported.Probe);

        // Update: clear the probe back to null (ICMP).
        var update = await _client.PutAsJsonAsync($"/api/v1/targets/{exported.Id}", new
        {
            id = exported.Id, name = exported.Name, address = exported.Address,
            profile = exported.Profile, probe = (string?)null,
        });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var updated = Assert.Single((await ExportAsync()).Targets, t => t.Name == "Modem");
        Assert.Null(updated.Probe);
    }

    [Fact]
    public async Task Probe_survives_snapshot_import()
    {
        await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Modem", address = "10.1.10.1", profile = "Public", probe = "tcp:80",
        });

        var doc = await ExportAsync();
        var import = await _client.PostAsJsonAsync("/api/v1/config/import", doc);
        Assert.Equal(HttpStatusCode.NoContent, import.StatusCode);
        Assert.Equal("tcp:80",
            Assert.Single((await ExportAsync()).Targets, t => t.Name == "Modem").Probe);

        var snapshot = JsonSerializer.Deserialize<Snapshot>(
            await _client.GetStringAsync("/api/v1/export/snapshot"), Web)!;
        Assert.Equal("tcp:80",
            Assert.Single(snapshot.Config.Targets, t => t.Name == "Modem").Probe);
    }

    [Fact]
    public async Task Uppercase_probe_is_normalized_to_canonical_form_on_write()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Modem", address = "10.1.10.1", profile = "Public", probe = "TCP:80",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var exported = Assert.Single((await ExportAsync()).Targets, t => t.Name == "Modem");
        Assert.Equal("tcp:80", exported.Probe);
    }

    [Fact]
    public async Task Impolite_public_probe_is_rejected_with_400()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "PublicHost", address = "8.8.8.8", profile = "Public", probe = "tcp:8080",
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var body = await create.Content.ReadAsStringAsync();
        Assert.Contains("port", body, StringComparison.OrdinalIgnoreCase);
    }
}
