using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class LegacyInstallTests : IDisposable
{
    private readonly string _app = Directory.CreateTempSubdirectory("legacy").FullName;
    public void Dispose() => Directory.Delete(_app, recursive: true);

    [Fact]
    public void Nothing_legacy_yields_nulls()
    {
        var v = LegacyInstall.Detect(_app, null);
        Assert.Null(v.ServerUrl);
        Assert.Null(v.ListenUrl);
    }

    [Fact]
    public void Reads_contray_json_from_the_old_watcher_or_tray_folder()
    {
        Directory.CreateDirectory(Path.Combine(_app, "Tray"));
        File.WriteAllText(Path.Combine(_app, "Tray", "contray.json"), """{ "url": "http://nas:9090" }""");
        Assert.Equal("http://nas:9090", LegacyInstall.Detect(_app, null).ServerUrl);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\DSoftCo\\ConnectionWatcher\\Watcher\\ConWatch.exe\" --urls http://0.0.0.0:8081 --Persistence:DbPath \"C:\\x\\conwatch.db\"", "http://0.0.0.0:8081")]
    [InlineData("\"C:\\app\\conwatch.exe\" serve", null)]
    [InlineData("\"C:\\old\\ConWatch.exe\"", null)]
    public void Reads_the_listen_url_from_an_old_service_binary_path(string binaryPath, string? expected) =>
        Assert.Equal(expected, LegacyInstall.Detect(_app, binaryPath).ListenUrl);
}
