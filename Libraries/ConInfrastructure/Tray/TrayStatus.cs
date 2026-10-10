using System.Text.Json;

namespace ConInfrastructure.Tray;

public enum TrayState
{
    Unreachable,   // service not answering
    Measuring,     // up, but no determinable verdict/states yet
    Healthy,       // incl. SiteIncident/InternetWeather: trouble out there is not YOUR internet
    Degraded,      // your link is degraded / some target unhappy
    Down           // link down / a target hard-down (no-verdict fallback)
}

/// <summary>A target marked critical, as the status reports it.</summary>
/// <param name="Bypassed">A HopBypassed note exists for it: traffic is passing it.</param>
public record TrayCritical(string Id, string Name, string State, bool Bypassed)
{
    public bool IsDown => State is "Down" or "Unreachable";
}

/// <summary>State, the human-readable evidence line, and the short labels of
/// unacknowledged admin notes (never part of the state: spec 2026-10-09 D3).</summary>
public record TrayInfo(TrayState State, string? Detail, IReadOnlyList<string> Notes)
{
    /// <summary>Targets marked critical (spec §4.7), any state.</summary>
    public IReadOnlyList<TrayCritical> Critical { get; init; } = [];
}

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
            var notes = status.TryGetProperty("warnings", out var warnings) &&
                        warnings.ValueKind == JsonValueKind.Array
                ? warnings.EnumerateArray()
                    .Where(w => !(w.TryGetProperty("acknowledged", out var a) && a.ValueKind == JsonValueKind.True))
                    .Select(NoteLabel).ToList()
                : new List<string>();
            var bypassed = status.TryGetProperty("warnings", out var ws) && ws.ValueKind == JsonValueKind.Array
                ? ws.EnumerateArray()
                    .Where(w => w.ValueKind == JsonValueKind.Object
                                && w.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                                && k.GetString() == "HopBypassed"
                                && w.TryGetProperty("targetId", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(w => w.GetProperty("targetId").GetString()!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            var critical = new List<TrayCritical>();
            if (status.TryGetProperty("targets", out var ts) && ts.ValueKind == JsonValueKind.Array)
                foreach (var t in ts.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object
                        || !t.TryGetProperty("critical", out var c) || c.ValueKind != JsonValueKind.True
                        || !Str(t, "id", out var id) || !Str(t, "name", out var name) || !Str(t, "state", out var st))
                        continue; // a malformed entry is skipped, never fatal
                    critical.Add(new TrayCritical(id, name, st, bypassed.Contains(id)));
                }
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
                return new TrayInfo(state, detail, notes) { Critical = critical };
            }

            // No verdict configured: derive from target states.
            if (!status.TryGetProperty("targets", out var targets) ||
                targets.GetArrayLength() == 0)
                return new TrayInfo(TrayState.Measuring, null, notes) { Critical = critical };
            var states = targets.EnumerateArray()
                .Where(t => !(t.TryGetProperty("scope", out var scope) && scope.GetString() == "Local"))
                .Select(t => (Name: t.GetProperty("name").GetString() ?? "",
                              State: t.GetProperty("state").GetString() ?? ""))
                .ToList();
            var bad = states.Where(s => s.State is "Down" or "Unreachable").ToList();
            if (bad.Count > 0)
                return new TrayInfo(TrayState.Down,
                    string.Join(", ", bad.Select(b => $"{b.Name}={b.State}")), notes) { Critical = critical };
            var shaky = states.Where(s => s.State is "Faltering" or "Recovering").ToList();
            if (shaky.Count > 0)
                return new TrayInfo(TrayState.Degraded,
                    string.Join(", ", shaky.Select(b => $"{b.Name}={b.State}")), notes) { Critical = critical };
            if (states.All(s => s.State is "Starting" or "Initializing"))
                return new TrayInfo(TrayState.Measuring, null, notes) { Critical = critical };
            return new TrayInfo(TrayState.Healthy, null, notes) { Critical = critical };
        }
        catch (JsonException)
        {
            return new TrayInfo(TrayState.Unreachable, null, []);
        }
    }

    private static bool Str(JsonElement e, string name, out string value)
    {
        var ok = e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String;
        value = ok ? p.GetString() ?? "" : "";
        return ok;
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

    /// <summary>A tooltip-sized label for one note.</summary>
    public static string NoteLabel(JsonElement warning)
    {
        var target = warning.TryGetProperty("targetName", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        var kind = warning.TryGetProperty("kind", out var k) ? k.GetString() : null;
        return kind switch
        {
            "HopBypassed" when target is not null => $"{target} isn't answering (traffic passes it)",
            "NeverAnswered" when target is not null => $"{target} never answers its probe",
            "UnprovableUp" => "Can't prove the internet is up beyond your ISP",
            _ => "ConnectionWatcher note",
        };
    }

    /// <summary>NotifyIcon.Text: state, evidence, then one line per note,
    /// truncated to the 127-character limit.</summary>
    public static string Tooltip(TrayInfo info)
    {
        var text = Describe(info.State);
        // Devices before the verdict detail: truncation cuts the detail first.
        foreach (var c in info.Critical.Where(c => c.IsDown))
            text += $"\n⚠ {c.Name} is {c.State}";
        if (info.Detail is { Length: > 0 })
            text += $"\n{info.Detail}";
        foreach (var note in info.Notes)
            text += $"\n🔧 {note}";
        const int max = 127;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
