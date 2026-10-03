using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ConWatch.Tests;

public class LocalScopeApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public LocalScopeApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_Local_target_is_created_and_listed_with_its_scope()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Internal", scope = "Local",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var list = await _client.GetStringAsync("/api/v1/targets");
        var nas = JsonSerializer.Deserialize<JsonElement>(list).EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "NAS");
        Assert.Equal("Local", nas.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task An_unknown_scope_is_a_client_error()
    {
        var response = await _client.PostAsync("/api/v1/targets", new StringContent(
            "{\"name\":\"NAS\",\"address\":\"203.0.113.9\",\"profile\":\"Internal\",\"scope\":\"Bogus\"}",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_undefined_numeric_scope_is_400_and_not_created()
    {
        var response = await _client.PostAsync("/api/v1/targets", new StringContent(
            "{\"name\":\"NAS\",\"address\":\"203.0.113.9\",\"profile\":\"Internal\",\"scope\":7}",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var list = await _client.GetStringAsync("/api/v1/targets");
        Assert.DoesNotContain(JsonSerializer.Deserialize<JsonElement>(list).EnumerateArray(),
            t => t.GetProperty("name").GetString() == "NAS");
    }

    [Fact]
    public async Task Status_reports_each_targets_scope()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();

        JsonElement status = default;
        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status");
            if (!response.IsSuccessStatusCode)
                return false; // 503 until the engine has started
            status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            return status.GetProperty("targets").EnumerateArray()
                .Any(t => t.GetProperty("name").GetString() == "NAS");
        });

        var scopes = status.GetProperty("targets").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("scope").GetString());
        Assert.Equal("Local", scopes["NAS"]);
        Assert.Equal("Verdict", scopes["Router"]);
    }

    [Fact]
    public async Task A_target_can_be_switched_Verdict_to_Local_and_back()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Cam", address = "203.0.113.20", profile = "Fast",
        })).EnsureSuccessStatusCode();

        async Task<(string Id, JsonElement Dto)> Cam()
        {
            var list = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/targets"));
            var dto = list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Cam");
            return (dto.GetProperty("id").GetString()!, dto);
        }

        var (id, _) = await Cam();
        var toLocal = await _client.PutAsJsonAsync($"/api/v1/targets/{id}", new
        {
            id, name = "Cam", address = "203.0.113.20", profile = "Fast", scope = "Local",
        });
        Assert.True(toLocal.IsSuccessStatusCode, await toLocal.Content.ReadAsStringAsync());
        Assert.Equal("Local", (await Cam()).Dto.GetProperty("scope").GetString());

        var back = await _client.PutAsJsonAsync($"/api/v1/targets/{id}", new
        {
            id, name = "Cam", address = "203.0.113.20", profile = "Fast", scope = "Verdict",
        });
        Assert.True(back.IsSuccessStatusCode, await back.Content.ReadAsStringAsync());
        Assert.Equal("Verdict", (await Cam()).Dto.GetProperty("scope").GetString());
    }
}
