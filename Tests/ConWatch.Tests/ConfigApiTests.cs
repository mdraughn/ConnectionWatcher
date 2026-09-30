using System.Net;
using System.Net.Http.Json;
using ConPersistence.Config;

namespace ConWatch.Tests;

public class ConfigApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ConfigApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    internal static async Task SeedHomeAsync(HttpClient client)
    {
        var profile = new ProfileDto
        {
            Name = "Fast", NormalIntervalSeconds = 0.02, BurstIntervalSeconds = 0.01,
            BurstMaxDurationSeconds = 5, HistorySize = 20, MinUsableSample = 5,
        };
        (await client.PostAsJsonAsync("/api/v1/profiles", profile)).EnsureSuccessStatusCode();
        foreach (var target in new TargetDto[]
        {
            new() { Name = "Router", Address = "192.168.1.1", Profile = "Fast" },
            new() { Name = "Google", Address = "8.8.8.8", Parent = "Router", Profile = "Fast" },
            new() { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router", Profile = "Fast" },
        })
        {
            (await client.PostAsJsonAsync("/api/v1/targets", target)).EnsureSuccessStatusCode();
        }
        var pool = new PoolDto { Name = "Internet", Members = ["Google", "Cloudflare"] };
        (await client.PostAsJsonAsync("/api/v1/pools", pool)).EnsureSuccessStatusCode();
    }

    internal static async Task Eventually(Func<Task<bool>> condition, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("Condition not met within timeout");
    }

    /// <summary>Null while the engine is mid-rebuild (503).</summary>
    private async Task<StatusDto?> GetStatus()
    {
        var response = await _client.GetAsync("/api/v1/status");
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<StatusDto>();
    }

    [Fact]
    public async Task Crud_seeds_engine_and_status_reaches_healthy()
    {
        await SeedHomeAsync(_client);

        await Eventually(async () =>
        {
            var status = await GetStatus();
            return status is not null
                && status.Targets.Any(t => t.Name == "Router" && t.State == "Responding")
                && status.Verdict is { State: "Healthy" };
        });

        StatusDto? final = null;
        await Eventually(async () => (final = await GetStatus()) is not null);
        Assert.Equal(3, final!.Targets.Count);
        Assert.Single(final.Pools);
        Assert.Contains(final.Targets, t => t.Name == "Google" && t.Address == "8.8.8.8");
    }

    [Fact]
    public async Task Unknown_parent_is_400_with_message()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/targets",
            new TargetDto { Name = "TV", Address = "192.168.1.50", Parent = "Ghost" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Ghost", body);
    }

    [Fact]
    public async Task Delete_referenced_target_is_409()
    {
        await SeedHomeAsync(_client);
        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        var router = targets!.Single(t => t.Name == "Router");
        var response = await _client.DeleteAsync($"/api/v1/targets/{router.Id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_id_put_is_404()
    {
        var response = await _client.PutAsJsonAsync($"/api/v1/targets/{Guid.NewGuid()}",
            new TargetDto { Name = "X", Address = "10.0.0.1" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Address_edit_requires_kind_and_records_marker()
    {
        await SeedHomeAsync(_client);
        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        var google = targets!.Single(t => t.Name == "Google");

        // Without addressChange: 400 naming the values.
        var bare = await _client.PutAsJsonAsync($"/api/v1/targets/{google.Id}",
            google with { Address = "8.8.4.4" });
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("correction", await bare.Content.ReadAsStringAsync());

        // With correction: succeeds and a HistoryReset marker lands in the log.
        var withKind = await _client.PutAsJsonAsync(
            $"/api/v1/targets/{google.Id}?addressChange=correction",
            google with { Address = "8.8.4.4" });
        Assert.Equal(HttpStatusCode.NoContent, withKind.StatusCode);

        var log = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/v1/transitions?subjectId={google.Id}&limit=50");
        Assert.Contains(log.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("trigger").GetString() == "HistoryReset");
    }

    [Fact]
    public async Task Insert_parent_reparents_children_over_http()
    {
        await SeedHomeAsync(_client);
        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        var childIds = targets!.Where(t => t.Name is "Google" or "Cloudflare")
            .Select(t => t.Id!.Value).ToList();

        var response = await _client.PostAsJsonAsync("/api/v1/targets/insert-parent", new
        {
            newTarget = new TargetDto { Name = "Modem", Address = "192.168.100.1", Profile = "Fast" },
            childIds,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var after = (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;
        Assert.Equal("Router", after.Single(t => t.Name == "Modem").Parent);
        Assert.Equal("Modem", after.Single(t => t.Name == "Google").Parent);
        Assert.Equal("Modem", after.Single(t => t.Name == "Cloudflare").Parent);
    }

    [Fact]
    public async Task Export_import_roundtrips_to_second_instance()
    {
        await SeedHomeAsync(_client);
        var doc = await _client.GetFromJsonAsync<ConfigDocument>("/api/v1/config/export");
        Assert.NotNull(doc);

        using var second = new ConWatchFactory();
        var secondClient = second.CreateClient();
        var import = await secondClient.PostAsJsonAsync("/api/v1/config/import", doc);
        Assert.Equal(HttpStatusCode.NoContent, import.StatusCode);

        var targets = await secondClient.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Equal(3, targets!.Count);
        var original = doc.Targets.Single(t => t.Name == "Google");
        Assert.Equal(original.Id, targets.Single(t => t.Name == "Google").Id);
    }

    internal sealed record StatusDto(VerdictDto? Verdict, List<TargetStatusDto> Targets, List<PoolStatusDto> Pools);
    internal sealed record VerdictDto(string State, string? Detail);
    internal sealed record TargetStatusDto(Guid Id, string Name, string? Address, string State,
        double SuccessRate, double AvgRtt, int SampleSize);
    internal sealed record PoolStatusDto(Guid Id, string Name, string State);
}
