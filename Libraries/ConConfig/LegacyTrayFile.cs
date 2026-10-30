using System.Text.Json;

namespace ConConfig;

/// <summary>The pre-0.10 tray file: { "url": ..., "notifications": ... }.</summary>
public static class LegacyTrayFile
{
    public static ConWatchConfig? TryRead(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            string? url = root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() is { } raw ? ConfigResolver.NormalizeServerUrl(raw) : null : null;
            bool? notifications = root.TryGetProperty("notifications", out var n) && n.ValueKind is JsonValueKind.True or JsonValueKind.False ? n.GetBoolean() : null;
            return new ConWatchConfig(ServerUrl: url is { Length: > 0 } ? url : null, Notifications: notifications);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
