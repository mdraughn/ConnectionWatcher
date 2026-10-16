using System.Net.Http.Json;

namespace ConWatch.Tests;

public class SettingsPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public SettingsPageTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_ASN_lookup_is_a_switch_that_reflects_the_stored_setting()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var html = await _client.GetStringAsync("/Settings");
        Assert.Contains("type=\"checkbox\" role=\"switch\" name=\"Traceroute.AsnLookup\"", html);
        Assert.DoesNotContain("<select name=\"Traceroute.AsnLookup\"", html);
        (await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "true" })).EnsureSuccessStatusCode();
        html = await _client.GetStringAsync("/Settings");
        Assert.Matches("name=\"Traceroute.AsnLookup\"[^>]*checked", html);
    }
}
