namespace ConInfrastructure.Tray;

/// <summary>
/// The tray can show one balloon at a time, so everything a poll wants to
/// say is folded into a single toast.
/// </summary>
public static class TrayToasts
{
    /// <summary>
    /// The internet toast for a poll (spec 2026-10-09 tray; 2026-10-10 §5.4): on a change of
    /// state, or of verdict between the two Down kinds. Never while measuring. The caller
    /// still gates on the first poll and the notifications setting.
    /// </summary>
    public static TrayToast? Internet(TrayInfo previous, TrayInfo next, string monitorUrl)
    {
        if (next.State == TrayState.Measuring || previous.State == TrayState.Measuring)
            return null;
        if (next.State == previous.State && next.NamesFailing == previous.NamesFailing)
            return null;

        if (next.State == TrayState.Down && next.NamesFailing)
        {
            var blame = (next.Detail ?? "").StartsWith("DnsDown: ", StringComparison.Ordinal)
                ? next.Detail![9..] : next.Detail is { Length: > 0 } d ? d : "your resolver isn't answering";
            return new TrayToast("Name lookups are failing", $"Your internet is up, but name lookups are failing — {blame}", ToastLevel.Error);
        }
        (string? title, ToastLevel level) = next.State switch
        {
            TrayState.Down => ("Internet DOWN", ToastLevel.Error),
            TrayState.Degraded => ("Internet degraded", ToastLevel.Warning),
            TrayState.Healthy => ("Internet healthy again", ToastLevel.Info),
            TrayState.Unreachable => ("ConnectionWatcher unreachable", ToastLevel.Warning),
            _ => (null, ToastLevel.Info),
        };
        if (title is null)
            return null;
        var body = next.State == TrayState.Unreachable
            ? $"The monitor at {monitorUrl} is not answering."
            : next.State == TrayState.Healthy && previous.NamesFailing ? "Name lookups are working again."
            : next.Detail is { Length: > 0 } ? next.Detail : TrayStatus.Describe(next);
        return new TrayToast(title, body, level);
    }

    /// <summary>Headline priority: critical failure, internet error/warning,
    /// critical recovery, internet info. The others follow as body lines.</summary>
    public static TrayToast? Combine(TrayToast? internet, IReadOnlyList<TrayToast> critical)
    {
        var all = new List<(TrayToast Toast, int Rank)>();
        if (internet is not null)
            all.Add((internet, internet.Level == ToastLevel.Info ? 1 : 3));
        all.AddRange(critical.Select(c => (c, c.Level == ToastLevel.Info ? 2 : 4)));
        if (all.Count == 0)
            return null;
        if (all.Count == 1)
            return all[0].Toast;

        var headline = all.MaxBy(a => a.Rank).Toast; // first of equals wins: internet, then critical order
        var body = string.Join('\n', new[] { headline.Body }
            .Concat(all.Where(a => !ReferenceEquals(a.Toast, headline)).Select(a => a.Toast.Title)));
        return new TrayToast(headline.Title, body, all.Max(a => a.Toast.Level), all.Any(a => a.Toast.OpenNotes));
    }
}
