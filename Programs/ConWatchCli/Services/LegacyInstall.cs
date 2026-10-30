using ConConfig;
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
            // Unreadable or a non-string url → nothing to migrate from this file.
            if (LegacyTrayFile.TryRead(Path.Combine(appDir, sub, "contray.json"))?.ServerUrl is { Length: > 0 } value)
            {
                server = value;
                break;
            }
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
