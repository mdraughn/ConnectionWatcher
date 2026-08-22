using System.Text.Json;

namespace ConTray;

public enum TrayState
{
    Unreachable,   // service not answering
    Measuring,     // up, but no determinable verdict/states yet
    Healthy,
    Degraded,      // weather / link degraded / some target unhappy
    Down           // link down / a target hard-down (no-verdict fallback)
}

/// <summary>
/// Pure mapping from the /api/v1/status JSON to a tray state. Verdict wins
/// when present; otherwise the worst target state decides.
/// </summary>
public static class TrayStatus
{
    public static TrayState FromStatusJson(string json)
    {
        try
        {
            var status = JsonSerializer.Deserialize<JsonElement>(json);
            if (status.TryGetProperty("verdict", out var verdict) &&
                verdict.ValueKind == JsonValueKind.Object)
            {
                return verdict.GetProperty("state").GetString() switch
                {
                    "Healthy" or "SiteIncident" => TrayState.Healthy,
                    "InternetWeather" or "LinkDegraded" => TrayState.Degraded,
                    "LinkDown" => TrayState.Down,
                    _ => TrayState.Measuring,
                };
            }

            // No verdict configured: derive from target states.
            if (!status.TryGetProperty("targets", out var targets) ||
                targets.GetArrayLength() == 0)
                return TrayState.Measuring;
            var states = targets.EnumerateArray()
                .Select(t => t.GetProperty("state").GetString() ?? "")
                .ToList();
            if (states.Any(s => s is "Down" or "Unreachable"))
                return TrayState.Down;
            if (states.Any(s => s is "Faltering" or "Recovering"))
                return TrayState.Degraded;
            if (states.All(s => s is "Starting" or "Initializing"))
                return TrayState.Measuring;
            return TrayState.Healthy;
        }
        catch (JsonException)
        {
            return TrayState.Unreachable;
        }
    }

    public static string Describe(TrayState state) => state switch
    {
        TrayState.Unreachable => "ConnectionWatcher service is not responding",
        TrayState.Measuring => "ConnectionWatcher — measuring…",
        TrayState.Healthy => "Internet: healthy",
        TrayState.Degraded => "Internet: degraded / weather",
        TrayState.Down => "Internet: DOWN",
        _ => "ConnectionWatcher",
    };
}
