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

    [Theory]
    [InlineData("/Settings", "Mqtt.Port", "1", "65535", "1883")]
    [InlineData("/Settings", "Heartbeat.IntervalMinutes", "1", "1440", "5")]
    [InlineData("/Data", "Retention.MinuteRetentionDays", "1", "3650", "90")]
    [InlineData("/Data", "Retention.HourlyRetentionMonths", "1", "120", "13")]
    [InlineData("/Data", "Retention.TransitionMonths", "1", "120", "24")]
    [InlineData("/Data", "Retention.RetentionRunHourUtc", "0", "23", "3")]
    public async Task Number_inputs_render_their_own_keys_range_and_default(
        string page, string key, string min, string max, string value)
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var html = await _client.GetStringAsync(page);
        var input = System.Text.RegularExpressions.Regex.Match(html, "<input name=\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"[^>]*>");
        Assert.True(input.Success, $"no input for {key}");
        Assert.Contains($"min=\"{min}\"", input.Value);
        Assert.Contains($"max=\"{max}\"", input.Value);
        Assert.Contains($"value=\"{value}\"", input.Value);
    }
}
