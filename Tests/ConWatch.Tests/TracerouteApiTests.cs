using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConInfrastructure.Asn;
using ConInfrastructure.Traceroute;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConWatch.Tests;

public class TracerouteApiTests : IDisposable
{
    /// <summary>Two intermediate hops then the destination.</summary>
    private sealed class FakeProbe : ITtlProbe
    {
        private static readonly string[] Hops = ["10.0.0.1", "198.51.100.11"];

        public Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct)
        {
            if (ttl <= Hops.Length)
                return Task.FromResult(new TtlProbeResult(IPAddress.Parse(Hops[ttl - 1]), ttl * 5, false, false));
            return Task.FromResult(new TtlProbeResult(destination, 25, true, false));
        }
    }

    /// <summary>Knows one address; everything else is a miss.</summary>
    private sealed class FakeAsn : IAsnLookup
    {
        public Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct) =>
            Task.FromResult<AsnInfo?>(address.ToString() == "198.51.100.11"
                ? new AsnInfo(64496, "EXAMPLE-ISP", "US") : null);
    }

    private sealed class Factory : ConWatchFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITtlProbe>();
                services.AddSingleton<ITtlProbe, FakeProbe>();
                services.RemoveAll<IAsnLookup>();
                services.AddSingleton<IAsnLookup, FakeAsn>();
            });
        }
    }

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Traceroute_returns_ordered_hops()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/traceroute", new { host = "8.8.8.8" });
        response.EnsureSuccessStatusCode();

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var hops = body.GetProperty("hops").EnumerateArray().ToList();
        Assert.Equal(3, hops.Count);
        Assert.Equal("10.0.0.1", hops[0].GetProperty("address").GetString());
        Assert.False(hops[0].GetProperty("reachedDestination").GetBoolean());
        Assert.Equal("8.8.8.8", hops[2].GetProperty("address").GetString());
        Assert.True(hops[2].GetProperty("reachedDestination").GetBoolean());
        Assert.False(body.GetProperty("asnLookupEnabled").GetBoolean());
        Assert.Equal("Internal", hops[0].GetProperty("kind").GetString());
        Assert.Equal("Path", hops[1].GetProperty("kind").GetString());
        Assert.Equal("Destination", hops[2].GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, hops[1].GetProperty("asn").ValueKind);
    }

    [Fact]
    public async Task Operator_column_appears_when_the_setting_is_on()
    {
        var client = _factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "true" });
        var response = await client.PostAsJsonAsync("/api/v1/traceroute", new { host = "8.8.8.8" });
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.True(body.GetProperty("asnLookupEnabled").GetBoolean());
        var hops = body.GetProperty("hops").EnumerateArray().ToList();
        Assert.Equal("EXAMPLE-ISP", hops[1].GetProperty("asName").GetString());
        Assert.Equal(64496, hops[1].GetProperty("asn").GetInt32());
    }

    [Fact]
    public async Task Empty_host_is_400()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/traceroute", new { host = "" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
