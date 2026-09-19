using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

public class ProbeTestEndpointTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ProbeTestEndpointTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Probe_test_reports_icmp_and_polite_tcp_candidates()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/targets/probe-test",
            new { address = "10.1.10.1" });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var probes = body.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("probe").GetString()).ToList();
        Assert.Contains("icmp", probes);
        Assert.Contains("tcp:80", probes);
        Assert.Contains("tcp:8443", probes);   // private address: extended list
        Assert.DoesNotContain("tcp:53", probes); // 53 is the public list
        Assert.All(body.GetProperty("results").EnumerateArray(),
            r => Assert.True(r.GetProperty("reply").GetBoolean())); // fake answers everything
    }

    [Fact]
    public async Task Probe_test_public_address_uses_polite_ports_only()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/targets/probe-test",
            new { address = "8.8.8.8", port = 8080 });
        var body = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync());
        var probes = body.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("probe").GetString()).ToList();
        Assert.Equal(["icmp", "tcp:80", "tcp:443", "tcp:53"], probes); // 8080 filtered by policy
    }

    [Fact]
    public async Task Probe_test_is_admin_gated()
    {
        await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        await _client.PostAsync("/api/v1/auth/logout", null);

        var response = await _client.PostAsJsonAsync("/api/v1/targets/probe-test",
            new { address = "10.1.10.1" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unresolvable_hostname_reports_the_resolver_reason()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/targets/probe-test",
            new { address = "nonexistent.invalid" });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("resolvedIp").ValueKind);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("resolutionError").GetString()));
    }

    [Fact]
    public async Task Name_that_resolves_only_to_ipv6_reports_no_ipv4_address()
    {
        var dns = new ConEngine.Tests.Fakes.FakeDomainNameResolverFactory();
        dns.Answers["v6only.example"] = [System.Net.IPAddress.Parse("2001:db8::1")];
        using var factory = new ConWatchFactory
        {
            TestServices = services =>
            {
                services.RemoveAll<ConCore.IDomainNameResolverFactory>();
                services.AddSingleton<ConCore.IDomainNameResolverFactory>(dns);
            },
        };
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/targets/probe-test",
            new { address = "v6only.example" });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("resolvedIp").ValueKind);
        Assert.Equal("The name resolved, but to no IPv4 address.", body.GetProperty("resolutionError").GetString());
        Assert.Empty(body.GetProperty("resolvedIps").EnumerateArray());
    }
}
