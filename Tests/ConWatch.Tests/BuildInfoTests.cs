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

    [Fact]
    public void Dev_builds_are_marked_as_such()
    {
        // Local and edge builds must never impersonate a release: Directory.Build.props
        // sets VersionSuffix=dev, and only a release build overrides Version from its tag.
        Assert.Contains("-dev", BuildInfo.ShortVersion);
        Assert.DoesNotContain("+", BuildInfo.ShortVersion); // metadata stays out of the UI string
    }
}
