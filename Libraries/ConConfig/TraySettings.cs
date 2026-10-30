namespace ConConfig;

/// <summary>
/// Server URL and notifications for the tray: per-user tray.json (override)
/// over the machine conwatch.json (installed default) over localhost. The
/// old contray-settings.json is read when tray.json does not exist yet and
/// is rewritten as tray.json on the first save. A tray.json that is not
/// valid config is kept aside as tray.json.corrupt (replacing an older one)
/// on the first save, the same policy as `conwatch config set`.
/// </summary>
public sealed class TraySettings
{
    public const string FallbackUrl = ConfigResolver.FallbackServerUrl;

    private readonly ConfigPaths _paths;
    private readonly ConWatchConfig? _machine;
    private ConWatchConfig _user;
    private bool _userFileCorrupt;   // tray.json is malformed: move it aside before the first write replaces it

    public TraySettings(ConfigPaths? paths = null)
    {
        _paths = paths ?? ConfigPaths.Default();
        _machine = ConfigFile.TryRead(_paths.MachineFile, out _);
        _user = ConfigFile.TryRead(_paths.UserFile, out var userError, out var unreadable)
            ?? LegacyTrayFile.TryRead(_paths.LegacyUserFile)
            ?? new ConWatchConfig();
        _userFileCorrupt = userError is not null && !unreadable;
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

    /// <summary>
    /// What the Settings dialog stores for a typed URL: null (clear the override) when the box is empty or
    /// holds the URL the tray would use anyway — the installed default, or the fallback when nothing is
    /// installed — ignoring case and a trailing slash; otherwise the trimmed URL, which is pinned. Typing a
    /// default therefore follows it if the install changes, instead of freezing today's value.
    /// </summary>
    public static string? ApplyUrl(string typed, string? installedDefault, string fallback)
    {
        var url = ConfigResolver.NormalizeServerUrl(typed);
        var standing = ConfigResolver.NormalizeServerUrl(installedDefault ?? fallback);
        return url.Length == 0 || string.Equals(url, standing, StringComparison.OrdinalIgnoreCase) ? null : url;
    }

    public void SetOverride(string url) => Save(_user with { ServerUrl = ConfigResolver.NormalizeServerUrl(url) });
    public void ClearOverride() => Save(_user with { ServerUrl = null });
    public void SetNotifications(bool enabled) => Save(_user with { Notifications = enabled });

    private void Save(ConWatchConfig next)
    {
        _user = next;
        try
        {
            if (_userFileCorrupt)
            {
                // If this move fails, the write below is skipped too: never replace a file we could not keep.
                if (File.Exists(_paths.UserFile))
                    File.Move(_paths.UserFile, _paths.UserFile + ".corrupt", overwrite: true);
                _userFileCorrupt = false;
            }
            ConfigFile.WriteAtomic(_paths.UserFile, next);
        }
        catch (Exception) { /* settings that fail to persist still apply for this session */ }
    }
}
