using System.Text.Json;

namespace ConWatch.Tests;

public class BuildInfoTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Health_reports_version_and_build_time()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");
        // 200 or 503 (engine still booting) — the stamp rides along either way.
        var body = JsonSerializer.Deserialize<JsonElement>(
            await response.Content.ReadAsStringAsync());

        var version = body.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(BuildInfo.Version, version);

        var builtUtc = body.GetProperty("builtUtc").GetDateTimeOffset();
        Assert.InRange(builtUtc, DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
