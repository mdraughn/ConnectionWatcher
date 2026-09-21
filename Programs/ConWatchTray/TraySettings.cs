using ConConfig;

namespace ConWatchTray;

/// <summary>
/// Server URL and notifications for the tray: per-user tray.json (override)
/// over the machine conwatch.json (installed default) over localhost. The
/// old contray-settings.json is read when tray.json does not exist yet and
/// is rewritten as tray.json on the first save.
/// </summary>
public sealed class TraySettings
{
    public const string FallbackUrl = ConfigResolver.FallbackServerUrl;

    private readonly ConfigPaths _paths;
    private readonly ConWatchConfig? _machine;
    private ConWatchConfig _user;

    public TraySettings(ConfigPaths? paths = null)
    {
        _paths = paths ?? ConfigPaths.Default();
        _machine = ConfigFile.TryRead(_paths.MachineFile, out _);
        _user = ConfigFile.TryRead(_paths.UserFile, out _)
            ?? LegacyTrayFile.TryRead(_paths.LegacyUserFile)
            ?? new ConWatchConfig();
    }

    public string? OverrideUrl => _user.ServerUrl;
    public bool NotificationsEnabled => ConfigResolver.Notifications(_user);
    public bool HasOverride => OverrideUrl is { Length: > 0 };

    /// <summary>The machine config's server URL (or its listen URL rewritten), if an install wrote one.</summary>
    public string? InstalledDefaultUrl =>
        ConfigResolver.ServerUrl(_paths, null, _machine) is { Source: ConfigSource.Machine } r ? r.Value : null;

    /// <summary>override > installed default > localhost fallback.</summary>
    public string EffectiveUrl => ConfigResolver.ServerUrl(_paths, _user, _machine).Value;

    public string EffectiveSource =>
        HasOverride ? "custom"
        : InstalledDefaultUrl is not null ? "installed default"
        : "built-in fallback";

    public void SetOverride(string url) => Save(_user with { ServerUrl = url.TrimEnd('/') });
    public void ClearOverride() => Save(_user with { ServerUrl = null });
    public void SetNotifications(bool enabled) => Save(_user with { Notifications = enabled });

    private void Save(ConWatchConfig next)
    {
        _user = next;
        try { ConfigFile.WriteAtomic(_paths.UserFile, next); }
        catch (Exception) { /* settings that fail to persist still apply for this session */ }
    }
}
