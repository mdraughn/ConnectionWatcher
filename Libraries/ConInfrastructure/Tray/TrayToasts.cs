namespace ConInfrastructure.Tray;

/// <summary>
/// The tray can show one balloon at a time, so everything a poll wants to
/// say is folded into a single toast.
/// </summary>
public static class TrayToasts
{
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
