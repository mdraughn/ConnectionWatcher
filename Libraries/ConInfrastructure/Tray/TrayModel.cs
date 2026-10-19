namespace ConInfrastructure.Tray;

/// <summary>
/// The tray's poll reducer: from each successive <see cref="TrayInfo"/> it decides whether the
/// icon, tooltip and notes menu need redrawing and which single balloon (if any) to show.
/// The shell keeps the HTTP call, the icon and the balloon. Not thread-safe: the tray calls
/// it from its UI timer only.
/// </summary>
/// <remarks>
/// The shell still maps an HTTP failure to Unreachable and still catches a malformed body
/// before calling <see cref="Apply"/> (TR5: not fixed here).
/// </remarks>
public sealed class TrayModel
{
    private TrayInfo _last = new(TrayState.Unreachable, null, []);
    private IReadOnlyList<string> _notes = [];
    private IReadOnlyList<TrayCritical> _criticalShown = [];
    private readonly CriticalWatch _critical = new();
    private bool _firstPoll = true;

    /// <summary>
    /// Folds one poll in. <c>Redraw</c> is true when the state, the failing-names flag, the notes or
    /// the critical list changed. <c>Toast</c> is the one combined balloon: never on the first poll
    /// or with notifications off, and never for a notes-only change (spec D3).
    /// </summary>
    /// <param name="monitorUrl">Shown in the "unreachable" toast.</param>
    public (bool Redraw, TrayToast? Toast) Apply(TrayInfo next, bool notificationsOn, string monitorUrl)
    {
        TrayToast? internetToast = null;
        bool stateChanged = next.State != _last.State || next.NamesFailing != _last.NamesFailing;
        bool notesChanged = !next.Notes.SequenceEqual(_notes);
        bool criticalChanged = !next.Critical.SequenceEqual(_criticalShown);
        bool redraw = stateChanged || notesChanged || criticalChanged;
        if (redraw)
        {
            var previous = _last;
            _last = next;
            _notes = next.Notes;
            _criticalShown = next.Critical;
            // Toast on changes a person would act on: down (link or name lookups), degraded,
            // recovered, monitor lost/back. Never on startup, never for measuring churn.
            if (stateChanged && !_firstPoll && notificationsOn)
                internetToast = TrayToasts.Internet(previous, next, monitorUrl);
        }

        // Critical devices (spec §4.7): their own failure and recovery toast, even while the
        // internet is fine. Always updated, so a failure seen while muted is not replayed later.
        var critical = _critical.Update(next, announce: !_firstPoll && notificationsOn);
        _firstPoll = false;
        // One balloon per poll: a second would replace the first.
        return (redraw, TrayToasts.Combine(internetToast, critical));
    }
}
