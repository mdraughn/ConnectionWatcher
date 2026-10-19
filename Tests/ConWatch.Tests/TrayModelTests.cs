using ConInfrastructure.Tray;

namespace ConWatch.Tests;

public class TrayModelTests
{
    private const string Url = "http://monitor.test:5000";

    private static TrayInfo Info(TrayState state, string? verdict = null, string? detail = null,
        string[]? notes = null, params TrayCritical[] critical) =>
        new(state, detail, notes ?? []) { Verdict = verdict, Critical = critical };

    private static TrayCritical Router(string state) => new("r1", "Router", state, Bypassed: false);

    private static readonly TrayInfo Healthy = Info(TrayState.Healthy, "Healthy");
    private static readonly TrayInfo DnsDown = Info(TrayState.Down, "DnsDown", "DnsDown: Your resolver isn't answering");
    private static readonly TrayInfo LinkDown = Info(TrayState.Down, "LinkDown", "LinkDown: Router is Down");

    [Fact]
    public void The_first_poll_never_toasts()
    {
        var model = new TrayModel();
        var (redraw, toast) = model.Apply(Info(TrayState.Down, "LinkDown", critical: Router("Down")), true, Url);
        Assert.True(redraw); // the icon still has to move off Unreachable
        Assert.Null(toast);

        // ...and the critical failure it saw is recorded, so the next poll does not announce it either.
        var (_, again) = model.Apply(Info(TrayState.Down, "LinkDown", critical: Router("Down")), true, Url);
        Assert.Null(again);
    }

    [Fact]
    public void Healthy_DnsDown_LinkDown_toasts_twice()
    {
        var model = new TrayModel();
        model.Apply(Healthy, true, Url);

        var (r1, dns) = model.Apply(DnsDown, true, Url);
        Assert.True(r1);
        Assert.NotNull(dns);
        Assert.Contains("lookups", dns.Title);

        // Both are State Down: only the verdict name tells them apart.
        var (r2, link) = model.Apply(LinkDown, true, Url);
        Assert.True(r2);
        Assert.NotNull(link);
        Assert.Contains("DOWN", link.Title);

        var (r3, same) = model.Apply(LinkDown, true, Url);
        Assert.False(r3);
        Assert.Null(same);
    }

    [Fact]
    public void A_notes_only_change_redraws_without_a_toast()
    {
        var model = new TrayModel();
        model.Apply(Healthy, true, Url);

        var (redraw, toast) = model.Apply(Info(TrayState.Healthy, "Healthy", notes: ["Inside isn't answering (traffic passes it)"]), true, Url);
        Assert.True(redraw);
        Assert.Null(toast);

        var (again, none) = model.Apply(Info(TrayState.Healthy, "Healthy", notes: ["Inside isn't answering (traffic passes it)"]), true, Url);
        Assert.False(again);
        Assert.Null(none);
    }

    [Fact]
    public void Notifications_off_suppresses_toasts_but_still_records_criticals()
    {
        var model = new TrayModel();
        model.Apply(Healthy, true, Url);

        // Internet down and a critical down, both while notifications are off: redraw, no toast.
        var (redraw, toast) = model.Apply(Info(TrayState.Down, "LinkDown", critical: Router("Down")), false, Url);
        Assert.True(redraw);
        Assert.Null(toast);

        // Turning them on does not replay the failure it already recorded.
        var (_, after) = model.Apply(Info(TrayState.Down, "LinkDown", critical: Router("Down")), true, Url);
        Assert.Null(after);
    }

    [Fact]
    public void A_recovery_after_reenabling_is_not_announced_as_a_failure()
    {
        var model = new TrayModel();
        model.Apply(Healthy, true, Url);
        model.Apply(Info(TrayState.Healthy, "Healthy", critical: Router("Down")), false, Url); // down while muted
        model.Apply(Info(TrayState.Healthy, "Healthy", critical: Router("Down")), true, Url);  // unmuted, still down

        var (_, toast) = model.Apply(Info(TrayState.Healthy, "Healthy", critical: Router("Responding")), true, Url);
        Assert.NotNull(toast);
        Assert.Contains("Router", toast.Title);
        Assert.Contains("answering again", toast.Title);
        Assert.Equal(ToastLevel.Info, toast.Level);
    }

    [Fact]
    public void An_internet_toast_and_a_critical_toast_share_one_balloon()
    {
        var model = new TrayModel();
        model.Apply(Healthy, true, Url);
        var (_, toast) = model.Apply(Info(TrayState.Down, "LinkDown", "LinkDown: x", critical: Router("Down")), true, Url);
        Assert.NotNull(toast);
        Assert.Contains("Router", toast.Title);     // critical failure outranks the internet toast
        Assert.Contains("Internet DOWN", toast.Body);
    }
}
