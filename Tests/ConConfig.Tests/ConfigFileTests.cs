using System.Text.Json;
using ConConfig;

namespace ConConfig.Tests;

public sealed class ConfigFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("conconfig").FullName;
    private string File(string name) => Path.Combine(_root, name);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Missing_file_reads_as_null_without_error()
    {
        var config = ConfigFile.TryRead(File("nope.json"), out var error);
        Assert.Null(config);
        Assert.Null(error);
    }

    [Fact]
    public void Malformed_file_reads_as_null_with_error_naming_the_path()
    {
        var path = File("bad.json");
        System.IO.File.WriteAllText(path, "{ not json");
        var config = ConfigFile.TryRead(path, out var error);
        Assert.Null(config);
        Assert.Contains(path, error);
    }

    [Fact]
    public void Malformed_json_is_reported_as_not_unreadable()
    {
        var path = File("bad.json");
        System.IO.File.WriteAllText(path, "{ not json");
        var config = ConfigFile.TryRead(path, out var error, out var unreadable);
        Assert.Null(config);
        Assert.NotNull(error);
        Assert.False(unreadable);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_reported_as_unreadable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = File("locked.json");
        System.IO.File.WriteAllText(path, """{ "listenUrl": "http://0.0.0.0:8080" }""");
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var config = ConfigFile.TryRead(path, out var error, out var unreadable);
        Assert.Null(config);
        Assert.True(unreadable);
        Assert.NotNull(error);
        Assert.Contains(path, error);
    }

    [Fact]
    public void Reads_known_properties_and_keeps_unknown_ones()
    {
        var path = File("conwatch.json");
        System.IO.File.WriteAllText(path,
            """{ "listenUrl": "http://0.0.0.0:9090", "dataDir": "D:\\cw", "serverUrl": "http://nas:9090", "notifications": false, "future": { "x": 1 } }""");
        var config = ConfigFile.TryRead(path, out var error)!;
        Assert.Null(error);
        Assert.Equal("http://0.0.0.0:9090", config.ListenUrl);
        Assert.Equal(@"D:\cw", config.DataDir);
        Assert.Equal("http://nas:9090", config.ServerUrl);
        Assert.False(config.Notifications);
        Assert.Equal(1, config.Extra["future"].GetProperty("x").GetInt32());
    }

    [Fact]
    public void Write_creates_the_directory_and_round_trips_including_unknown_properties()
    {
        var path = File(Path.Combine("sub", "conwatch.json"));
        var extra = new Dictionary<string, JsonElement>
        {
            ["future"] = JsonDocument.Parse("""{"x":1}""").RootElement.Clone(),
        };
        ConfigFile.WriteAtomic(path, new ConWatchConfig(ListenUrl: "http://0.0.0.0:8080") { Extra = extra });
        var back = ConfigFile.TryRead(path, out _)!;
        Assert.Equal("http://0.0.0.0:8080", back.ListenUrl);
        Assert.Null(back.DataDir);
        Assert.Equal(1, back.Extra["future"].GetProperty("x").GetInt32());
        Assert.DoesNotContain("dataDir", System.IO.File.ReadAllText(path)); // nulls are omitted
    }

    [Fact]
    public void Write_replaces_an_existing_file_and_leaves_no_temp_file()
    {
        var path = File("conwatch.json");
        ConfigFile.WriteAtomic(path, new ConWatchConfig(ServerUrl: "http://a"));
        ConfigFile.WriteAtomic(path, new ConWatchConfig(ServerUrl: "http://b"));
        Assert.Equal("http://b", ConfigFile.TryRead(path, out _)!.ServerUrl);
        Assert.Single(Directory.GetFiles(_root));
    }
}
