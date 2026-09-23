using ConConfig;

namespace ConConfig.Tests;

public sealed class LegacyTrayFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("legacytray").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Maps_url_and_notifications()
    {
        var path = Path.Combine(_root, "contray-settings.json");
        File.WriteAllText(path, """{ "url": "http://nas:8080/", "notifications": false }""");
        var c = LegacyTrayFile.TryRead(path)!;
        Assert.Equal("http://nas:8080", c.ServerUrl);
        Assert.False(c.Notifications);
    }

    [Fact]
    public void Missing_or_malformed_is_null()
    {
        Assert.Null(LegacyTrayFile.TryRead(Path.Combine(_root, "none.json")));
        var bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "nope");
        Assert.Null(LegacyTrayFile.TryRead(bad));
    }
}
