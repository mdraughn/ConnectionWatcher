using System.Net.Http.Json;
using System.Text.Json;

namespace ConWatch.Tests;

public class StatusShapeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public StatusShapeTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static string[] Keys(JsonElement e) =>
        e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Live_status_shape_is_pinned()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        JsonElement status = default;
        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status");
            if (!response.IsSuccessStatusCode)
                return false;
            status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            return status.GetProperty("verdict").ValueKind == JsonValueKind.Object
                && status.GetProperty("targets").GetArrayLength() == 3
                && status.GetProperty("pools").GetArrayLength() == 1;
        });

        Assert.Equal(new[] { "enabled", "pools", "targets", "verdict", "warnings" }, Keys(status));
        Assert.Equal(JsonValueKind.Array, status.GetProperty("warnings").ValueKind);
        var sample = JsonSerializer.SerializeToElement(
            new StatusWarning("k", "HopBypassed", Guid.Empty, "Router", "m", DateTimeOffset.UnixEpoch, false),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "acknowledged", "key", "kind", "message", "since", "targetId", "targetName" },
            Keys(sample));
        Assert.Equal(new[] { "detail", "operator", "state" }, Keys(status.GetProperty("verdict")));
        Assert.Equal(new[] { "address", "assumed", "avgRtt", "critical", "explainedBy", "id", "name",
                "operator", "pingCount", "probe", "sampleSize", "scope", "state", "successRate" },
            Keys(status.GetProperty("targets")[0]));
        Assert.Equal(new[] { "id", "name", "state" }, Keys(status.GetProperty("pools")[0]));
    }

    [Fact]
    public async Task Status_reports_which_targets_are_critical()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var targets = (await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets"))!;
        var router = targets.Single(t => t.Name == "Router");
        (await _client.PutAsJsonAsync($"/api/v1/targets/{router.Id}", router with { Critical = true }))
            .EnsureSuccessStatusCode();

        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status");
            if (!response.IsSuccessStatusCode)
                return false;
            var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            var rows = status.GetProperty("targets").EnumerateArray().ToList();
            return rows.Count == 3
                && rows.Single(t => t.GetProperty("name").GetString() == "Router").GetProperty("critical").GetBoolean()
                && !rows.Single(t => t.GetProperty("name").GetString() == "Google").GetProperty("critical").GetBoolean();
        });
    }

    [Fact]
    public async Task Disabled_status_body_is_pinned()
    {
        (await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" })).EnsureSuccessStatusCode();

        var body = "";
        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status");
            body = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode && body.Contains("\"enabled\":false");
        });

        Assert.Equal("""{"enabled":false,"verdict":null,"targets":[],"pools":[],"warnings":[]}""", body);
    }
}
