using System.Text.Json;

namespace ConWatchTray;

public enum TrayState
{
    Unreachable,   // service not answering
    Measuring,     // up, but no determinable verdict/states yet
    Healthy,       // incl. SiteIncident/InternetWeather: trouble out there is not YOUR internet
    Degraded,      // your link is degraded / some target unhappy
    Down           // link down / a target hard-down (no-verdict fallback)
}

/// <summary>State plus the human-readable evidence line, for tooltips and toasts.</summary>
public record TrayInfo(TrayState State, string? Detail);

/// <summary>
/// Pure mapping from the /api/v1/status JSON to a tray state. Verdict wins
/// when present; otherwise the worst target state decides.
/// </summary>
public static class TrayStatus
{
    public static TrayInfo FromStatusJson(string json)
    {
        try
        {
            var status = JsonSerializer.Deserialize<JsonElement>(json);
            if (status.TryGetProperty("verdict", out var verdict) &&
                verdict.ValueKind == JsonValueKind.Object)
            {
                var detail = verdict.TryGetProperty("detail", out var d) ? d.GetString() : null;
                var state = verdict.GetProperty("state").GetString() switch
                {
                    // Same answer the web slab gives: weather and site incidents
                    // mean YOUR internet is up — no amber, no toast.
                    "Healthy" or "SiteIncident" or "InternetWeather" => TrayState.Healthy,
                    "LinkDegraded" => TrayState.Degraded,
                    "LinkDown" => TrayState.Down,
                    _ => TrayState.Measuring,
                };
                return new TrayInfo(state, detail);
            }

            // No verdict configured: derive from target states.
            if (!status.TryGetProperty("targets", out var targets) ||
                targets.GetArrayLength() == 0)
                return new TrayInfo(TrayState.Measuring, null);
            var states = targets.EnumerateArray()
                .Where(t => !(t.TryGetProperty("scope", out var scope) && scope.GetString() == "Local"))
                .Select(t => (Name: t.GetProperty("name").GetString() ?? "",
                              State: t.GetProperty("state").GetString() ?? ""))
                .ToList();
            var bad = states.Where(s => s.State is "Down" or "Unreachable").ToList();
            if (bad.Count > 0)
                return new TrayInfo(TrayState.Down,
                    string.Join(", ", bad.Select(b => $"{b.Name}={b.State}")));
            var shaky = states.Where(s => s.State is "Faltering" or "Recovering").ToList();
            if (shaky.Count > 0)
                return new TrayInfo(TrayState.Degraded,
                    string.Join(", ", shaky.Select(b => $"{b.Name}={b.State}")));
            if (states.All(s => s.State is "Starting" or "Initializing"))
                return new TrayInfo(TrayState.Measuring, null);
            return new TrayInfo(TrayState.Healthy, null);
        }
        catch (JsonException)
        {
            return new TrayInfo(TrayState.Unreachable, null);
        }
    }

    public static string Describe(TrayState state) => state switch
    {
        TrayState.Unreachable => "ConnectionWatcher service is not responding",
        TrayState.Measuring => "ConnectionWatcher — measuring…",
        TrayState.Healthy => "Internet: healthy",
        TrayState.Degraded => "Internet: degraded",
        TrayState.Down => "Internet: DOWN",
        _ => "ConnectionWatcher",
    };
}
