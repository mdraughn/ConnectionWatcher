using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConWatch.Tests;

public class HistoryScopeApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public HistoryScopeApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private async Task SeedWithLocalNasAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        // Both a verdict-scope and the Local target must have recorded a transition.
        await ConfigApiTests.Eventually(async () =>
        {
            var items = (await GetJson("/api/v1/transitions?limit=200")).GetProperty("items");
            var names = items.EnumerateArray().Select(i => i.GetProperty("subjectName").GetString()).ToHashSet();
            return names.Contains("NAS") && names.Contains("Router");
        });
    }

    [Fact]
    public async Task Scope_filters_transitions_and_each_item_reports_its_scope()
    {
        await SeedWithLocalNasAsync();

        var all = (await GetJson("/api/v1/transitions?limit=200")).GetProperty("items").EnumerateArray().ToList();
        var local = (await GetJson("/api/v1/transitions?scope=local&limit=200")).GetProperty("items").EnumerateArray().ToList();
        var internet = (await GetJson("/api/v1/transitions?scope=internet&limit=200")).GetProperty("items").EnumerateArray().ToList();

        Assert.Contains(all, i => i.GetProperty("subjectName").GetString() == "NAS");
        Assert.NotEmpty(local);
        Assert.All(local, i =>
        {
            Assert.Equal("NAS", i.GetProperty("subjectName").GetString());
            Assert.Equal("Local", i.GetProperty("scope").GetString());
        });
        Assert.NotEmpty(internet);
        Assert.DoesNotContain(internet, i => i.GetProperty("subjectName").GetString() == "NAS");
        Assert.All(internet, i => Assert.Equal("Verdict", i.GetProperty("scope").GetString()));
    }

    [Fact]
    public async Task An_unknown_scope_is_a_400_on_transitions_and_glitches()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/transitions?scope=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/glitches?scope=bogus")).StatusCode);
    }

    [Fact]
    public async Task Glitches_accept_every_legal_scope()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var body = await GetJson("/api/v1/glitches?scope=internet");
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        // No glitches exist yet; the filter itself must be accepted (200) for every legal value.
        foreach (var scope in new[] { "all", "internet", "local" })
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/glitches?scope={scope}")).StatusCode);
    }

    [Fact]
    public async Task A_deleted_target_is_Unknown_under_all_and_off_both_filters() // Review Focus 1, 2
    {
        await SeedWithLocalNasAsync();
        var targets = await GetJson("/api/v1/targets");
        var nasId = targets.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "NAS").GetProperty("id").GetString();
        (await _client.DeleteAsync($"/api/v1/targets/{nasId}")).EnsureSuccessStatusCode();

        var all = (await GetJson("/api/v1/transitions?limit=200")).GetProperty("items").EnumerateArray().ToList();
        var nas = all.Where(i => i.GetProperty("subjectName").GetString() == "NAS").ToList();
        Assert.NotEmpty(nas);
        Assert.All(nas, i => Assert.Equal("Unknown", i.GetProperty("scope").GetString()));
        var local = (await GetJson("/api/v1/transitions?scope=local&limit=200")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(local, i => i.GetProperty("subjectName").GetString() == "NAS");
        var internet = (await GetJson("/api/v1/transitions?scope=internet&limit=200")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(internet, i => i.GetProperty("subjectName").GetString() == "NAS");
        Assert.All(internet, i => Assert.NotEqual("Unknown", i.GetProperty("scope").GetString()));
        Assert.Contains(internet, i => i.GetProperty("subjectName").GetString() == "Router"); // live targets unaffected
    }
}
