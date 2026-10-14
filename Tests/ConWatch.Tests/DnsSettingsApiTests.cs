using System.Net;
using System.Net.Http.Json;
using ConPersistence.Settings;

namespace ConWatch.Tests;

public class DnsSettingsApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public DnsSettingsApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Dns_keys_are_validated_and_the_retired_list_is_not_editable()
    {
        var ok = await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            [SettingKeys.DnsEnabled] = "false",
            [SettingKeys.DnsLookupNames] = "[{\"name\":\"example.org\"}]",
        });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var badFlag = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [SettingKeys.DnsEnabled] = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, badFlag.StatusCode);

        var badJson = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [SettingKeys.DnsLookupNames] = "not json" });
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);

        var retired = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [SettingKeys.DnsRetiredResolvers] = "10.0.0.1" });
        Assert.Equal(HttpStatusCode.BadRequest, retired.StatusCode);
    }
}
