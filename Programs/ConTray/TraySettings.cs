using System.Text.Json;

namespace ConTray;

/// <summary>
/// Connection settings with three layers: a per-user override
/// (%APPDATA%\ConnectionWatcher\contray-settings.json), the installed default
/// (contray.json beside the exe, written by an installer), and the localhost
/// fallback. The override never touches the installed default, so "use
/// installed default" is always recoverable.
/// </summary>
public sealed class TraySettings
{
    public const string FallbackUrl = "http://localhost:8080";

    private readonly string _userSettingsPath;
    private readonly string _installedDefaultPath;

    public string? OverrideUrl { get; private set; }
    public bool NotificationsEnabled { get; private set; } = true;

    public TraySettings(string? userSettingsPath = null, string? installedDefaultPath = null)
    {
        _userSettingsPath = userSettingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DSoftCo", "ConnectionWatcher", "contray-settings.json");
        _installedDefaultPath = installedDefaultPath ??
            Path.Combine(AppContext.BaseDirectory, "contray.json");
        Load();
    }

    /// <summary>The installer-provided default URL, if an installer wrote one.</summary>
    public string? InstalledDefaultUrl
    {
        get
        {
            var url = ReadUrlFile(_installedDefaultPath);
            return url is { Length: > 0 } ? url.TrimEnd('/') : null;
        }
    }

    public bool HasOverride => OverrideUrl is { Length: > 0 };

    /// <summary>override > installed default > localhost fallback.</summary>
    public string EffectiveUrl =>
        OverrideUrl is { Length: > 0 } urlOverride ? urlOverride
        : InstalledDefaultUrl ?? FallbackUrl;

    public string EffectiveSource =>
        HasOverride ? "custom"
        : InstalledDefaultUrl is not null ? "installed default"
        : "built-in fallback";

    public void SetOverride(string url)
    {
        OverrideUrl = url.TrimEnd('/');
        Save();
    }

    public void ClearOverride()
    {
        OverrideUrl = null;
        Save();
    }

    public void SetNotifications(bool enabled)
    {
        NotificationsEnabled = enabled;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_userSettingsPath))
                return;
            var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(_userSettingsPath));
            if (json.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } value)
                OverrideUrl = value.TrimEnd('/');
            if (json.TryGetProperty("notifications", out var toasts))
                NotificationsEnabled = toasts.ValueKind != JsonValueKind.False;
        }
        catch
        {
            // Unreadable settings behave like none.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_userSettingsPath)!);
            var payload = JsonSerializer.Serialize(new
            {
                url = OverrideUrl,
                notifications = NotificationsEnabled,
            });
            File.WriteAllText(_userSettingsPath, payload);
        }
        catch
        {
            // Settings that fail to persist still apply for this session.
        }
    }

    private static string? ReadUrlFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
            return json.TryGetProperty("url", out var url) ? url.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
