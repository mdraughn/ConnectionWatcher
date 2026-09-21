using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConWatchCli.Services;

public sealed record LegacyValues(string? ServerUrl, string? ListenUrl);

/// <summary>
/// Reads what the pre-0.10 layout left behind: contray.json beside the old
/// exes, and the --urls baked into the old service's binary path. Used once,
/// when no machine config exists yet.
/// </summary>
public static partial class LegacyInstall
{
    public static LegacyValues Detect(string appDir, string? serviceBinaryPath)
    {
        string? server = null;
        foreach (var sub in new[] { "Watcher", "Tray" })
        {
            var path = Path.Combine(appDir, sub, "contray.json");
            if (!File.Exists(path)) continue;
            try
            {
                var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } value)
                {
                    server = value.TrimEnd('/');
                    break;
                }
            }
            catch (JsonException) { /* unreadable → nothing to migrate */ }
        }

        string? listen = null;
        if (serviceBinaryPath is not null && serviceBinaryPath.Contains("ConWatch.exe", StringComparison.OrdinalIgnoreCase))
        {
            var m = UrlsArgument().Match(serviceBinaryPath);
            if (m.Success) listen = m.Groups[1].Value;
        }
        return new LegacyValues(server, listen);
    }

    [GeneratedRegex("""--urls\s+"?([^\s"]+)"?""")]
    private static partial Regex UrlsArgument();
}
