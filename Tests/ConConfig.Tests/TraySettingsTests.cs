using ConConfig;

namespace ConConfig.Tests;

public sealed class TraySettingsTests : IDisposable
{
    private const string Nas = "http://nas.example.com:8080";

    private readonly string _root = Directory.CreateTempSubdirectory("traysettings").FullName;
    private readonly ConfigPaths _paths;

    public TraySettingsTests()
    {
        _paths = ConfigPaths.UnderTemp(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.UserFile)!);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Falls_back_to_the_legacy_file_and_migrates_on_first_save()
    {
        File.WriteAllText(_paths.LegacyUserFile, """{ "url": "http://nas.example.com:8080/", "notifications": false }""");

        var settings = new TraySettings(_paths);

        Assert.Equal(Nas, settings.EffectiveUrl);
        Assert.Equal("custom", settings.EffectiveSource);
        Assert.False(settings.NotificationsEnabled);
        Assert.False(File.Exists(_paths.UserFile));            // reading alone writes nothing

        settings.SetNotifications(true);

        var migrated = ConfigFile.TryRead(_paths.UserFile, out var error);
        Assert.Null(error);
        Assert.Equal(Nas, migrated!.ServerUrl);                 // the legacy URL came across
        Assert.True(migrated.Notifications);

        // From now on tray.json wins: a later change to the legacy file is ignored.
        File.WriteAllText(_paths.LegacyUserFile, """{ "url": "http://old.example.com:1/" }""");
        var reread = new TraySettings(_paths);
        Assert.Equal(Nas, reread.EffectiveUrl);
        Assert.True(reread.NotificationsEnabled);
    }

    [Fact]
    public void ApplyUrl_clears_instead_of_pinning_a_default()
    {
        const string fallback = TraySettings.FallbackUrl;

        // Typing the installed default (any case, trailing slash, spaces) clears the override.
        Assert.Null(TraySettings.ApplyUrl(" HTTP://NAS.example.com:8080/ ", Nas, fallback));
        // With no installed default, typing the built-in fallback clears it too.
        Assert.Null(TraySettings.ApplyUrl(fallback.ToUpperInvariant() + "/", null, fallback));
        // An empty box clears.
        Assert.Null(TraySettings.ApplyUrl("   ", Nas, fallback));

        // Anything else is pinned, trimmed.
        Assert.Equal("http://other.example.com:9000",
            TraySettings.ApplyUrl(" http://other.example.com:9000/ ", Nas, fallback));
        // When an install points elsewhere, localhost is a real choice and is pinned (today's rule).
        Assert.Equal(fallback, TraySettings.ApplyUrl(fallback + "/", Nas, fallback));

        // And the cleared override really falls back to the installed default.
        ConfigFile.WriteAtomic(_paths.MachineFile, new ConWatchConfig(ServerUrl: Nas));
        var settings = new TraySettings(_paths);
        settings.SetOverride("http://other.example.com:9000");
        Assert.Equal("custom", settings.EffectiveSource);
        if (TraySettings.ApplyUrl(Nas + "/", settings.InstalledDefaultUrl, fallback) is { } pinned)
            settings.SetOverride(pinned);
        else
            settings.ClearOverride();
        Assert.Equal("installed default", settings.EffectiveSource);
        Assert.Null(ConfigFile.TryRead(_paths.UserFile, out _)!.ServerUrl);
    }

    [Fact]
    public void A_corrupt_tray_json_is_moved_aside_to_corrupt()
    {
        File.WriteAllText(_paths.UserFile, "{ not json");
        File.WriteAllText(_paths.UserFile + ".corrupt", "an older casualty");   // replaced, as the CLI does

        var settings = new TraySettings(_paths);

        Assert.Equal(TraySettings.FallbackUrl, settings.EffectiveUrl);
        Assert.Equal("{ not json", File.ReadAllText(_paths.UserFile));          // reading alone moves nothing

        settings.SetNotifications(false);

        Assert.Equal("{ not json", File.ReadAllText(_paths.UserFile + ".corrupt"));
        var rewritten = ConfigFile.TryRead(_paths.UserFile, out var error);
        Assert.Null(error);
        Assert.False(rewritten!.Notifications);
    }
}
