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
}
