using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

/// <summary>The Targets form's Test button: probes plus, for a hostname, the whole DNS answer set.</summary>
public class ProbeTestApiTests : IDisposable
{
    private readonly FakeDomainNameResolverFactory _dns = new();
    private readonly ConWatchFactory _factory;
    private readonly HttpClient _client;

    public ProbeTestApiTests()
    {
        _factory = new ConWatchFactory
        {
            TestServices = services =>
            {
                services.RemoveAll<IDomainNameResolverFactory>();
                services.AddSingleton<IDomainNameResolverFactory>(_dns);
            },
        };
        _client = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Hostname_test_reports_every_address_in_the_answer_set()
    {
        _dns.Answers["site.example"] = [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("203.0.113.20")];

        var response = await _client.PostAsJsonAsync("/api/v1/targets/probe-test", new { address = "site.example" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        Assert.Equal("203.0.113.10", json.GetProperty("resolvedIp").GetString());
        var all = json.GetProperty("resolvedIps").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["203.0.113.10", "203.0.113.20"], all);
    }

    [Fact]
    public async Task Literal_address_has_a_single_entry_answer_set()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/targets/probe-test", new { address = "192.168.77.1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

        var all = json.GetProperty("resolvedIps").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["192.168.77.1"], all);
    }
}
