namespace ConInfrastructure.Tray;

/// <summary>A toast the tray should show.</summary>
public record TrayToast(string Title, string Body);

/// <summary>
/// Decides the critical-target toasts (spec 2026-10-09 §4.7) from successive
/// status polls: each failure is announced once and its recovery once, by
/// remembering what was announced rather than comparing to the last poll
/// (a released target recovers through Starting; a failed poll is no news).
/// Not thread-safe: the tray calls it from its UI timer only.
/// </summary>
public class CriticalWatch
{
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="announce">False for the first poll after the tray starts:
    /// record what is already down without toasting it.</param>
    public IReadOnlyList<TrayToast> Update(TrayInfo next, bool announce)
    {
        if (next.State == TrayState.Unreachable)
            return []; // no data: neither a failure nor a recovery

        var toasts = new List<TrayToast>();
        var present = next.Critical.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _announced.RemoveWhere(id => !present.Contains(id)); // deleted or un-marked: drop silently

        foreach (var c in next.Critical)
        {
            if (c.IsDown && !_announced.Contains(c.Id))
            {
                _announced.Add(c.Id);
                if (announce)
                    toasts.Add(new TrayToast($"{c.Name} is not answering", FailureBody(c, next.State)));
            }
            else if (c.State is "Responding" or "Recovering" or "Faltering" && _announced.Remove(c.Id) && announce)
            {
                toasts.Add(new TrayToast($"{c.Name} is answering again", "Marked critical in ConnectionWatcher."));
            }
        }
        return toasts;
    }

    private static string FailureBody(TrayCritical c, TrayState internet) =>
        c.Bypassed ? "Traffic is still passing it, so your internet is up — see ConnectionWatcher notes."
        : internet == TrayState.Healthy ? "Your internet is still up."
        // An ISP outage usually leaves the router answering: one that is not
        // means your own equipment (Mark, 2026-10-10).
        : internet == TrayState.Down ? $"The internet is down too — check {c.Name} itself."
        : "Marked critical in ConnectionWatcher.";
}
