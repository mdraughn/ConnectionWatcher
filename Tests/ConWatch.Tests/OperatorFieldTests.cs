using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence.Config;
using ConPersistence.Snapshots;

namespace ConWatch.Tests;

/// <summary>ISP naming: the optional Operator tag on targets.</summary>
public class OperatorFieldTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public OperatorFieldTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<ConfigDocument> ExportAsync() =>
        JsonSerializer.Deserialize<ConfigDocument>(
            await _client.GetStringAsync("/api/v1/config/export"), Web)!;

    [Fact]
    public async Task Operator_round_trips_through_create_update_and_export()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Comcast Edge",
            address = "198.51.100.201",
            profile = "External",
            @operator = "Comcast",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var exported = Assert.Single((await ExportAsync()).Targets, t => t.Name == "Comcast Edge");
        Assert.Equal("Comcast", exported.Operator);

        // Update: change the operator without touching the address.
        var update = await _client.PutAsJsonAsync($"/api/v1/targets/{exported.Id}", new
        {
            id = exported.Id,
            name = exported.Name,
            address = exported.Address,
            profile = exported.Profile,
            @operator = "  Easy Fiber  ", // trimmed on save
        });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var updated = Assert.Single((await ExportAsync()).Targets, t => t.Name == "Comcast Edge");
        Assert.Equal("Easy Fiber", updated.Operator);

        // Clearing: empty string stores as null.
        await _client.PutAsJsonAsync($"/api/v1/targets/{exported.Id}", new
        {
            id = exported.Id, name = exported.Name, address = exported.Address,
            profile = exported.Profile, @operator = "",
        });
        Assert.Null(Assert.Single((await ExportAsync()).Targets, t => t.Name == "Comcast Edge").Operator);
    }

    [Fact]
    public async Task Report_title_names_the_trunk_operator()
    {
        await ConfigApiTests.SeedHomeAsync(_client); // Router -> anchors + pool "Internet"
        var trunk = Assert.Single(
            (await ExportAsync()).Targets, t => t.Name == "Router");
        var put = await _client.PutAsJsonAsync($"/api/v1/targets/{trunk.Id}", new
        {
            id = trunk.Id, name = trunk.Name, address = trunk.Address,
            parent = trunk.Parent, profile = trunk.Profile, @operator = "Comcast",
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("Comcast",
            Assert.Single((await ExportAsync()).Targets, t => t.Name == "Router").Operator);

        var html = await _client.GetStringAsync("/Report");
        // Razor entity-encodes the dash; assert on the stable parts.
        Assert.Contains("Internet connection report", html);
        Assert.Contains("Comcast service", html);
    }

    [Fact]
    public async Task Operator_survives_config_import_and_snapshot_round_trip()
    {
        await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Edge", address = "198.51.100.201", profile = "External", @operator = "Comcast",
        });

        var doc = await ExportAsync();
        var import = await _client.PostAsJsonAsync("/api/v1/config/import", doc);
        Assert.Equal(HttpStatusCode.NoContent, import.StatusCode);
        Assert.Equal("Comcast",
            Assert.Single((await ExportAsync()).Targets, t => t.Name == "Edge").Operator);

        var snapshot = JsonSerializer.Deserialize<Snapshot>(
            await _client.GetStringAsync("/api/v1/export/snapshot"), Web)!;
        Assert.Equal("Comcast",
            Assert.Single(snapshot.Config.Targets, t => t.Name == "Edge").Operator);
    }
}
