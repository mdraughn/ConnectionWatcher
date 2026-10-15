using ConInfrastructure.Tray;

namespace ConWatch.Tests;

public class TrayToastsTests
{
    private static TrayToast Fail(string name, bool notes = false) =>
        new($"{name} is not answering", "Your internet is still up.", ToastLevel.Warning, notes);

    private static TrayToast Back(string name) =>
        new($"{name} is answering again", "Marked critical in ConnectionWatcher.", ToastLevel.Info);

    [Fact]
    public void Nothing_yields_null_and_one_toast_passes_through_unchanged()
    {
        Assert.Null(TrayToasts.Combine(null, []));
        var one = Fail("Router");
        Assert.Same(one, TrayToasts.Combine(null, [one]));
        var net = new TrayToast("Internet DOWN", "d", ToastLevel.Error);
        Assert.Same(net, TrayToasts.Combine(net, []));
    }

    [Fact]
    public void Three_critical_failures_make_one_toast_naming_all_three()
    {
        var t = TrayToasts.Combine(null, [Fail("Switch"), Fail("Router"), Fail("NAS")])!;
        Assert.Equal("Switch is not answering", t.Title);
        Assert.Contains("Router is not answering", t.Body);
        Assert.Contains("NAS is not answering", t.Body);
        Assert.StartsWith("Your internet is still up.", t.Body);
    }

    [Fact]
    public void Two_recoveries_combine_into_one_info_toast_with_both_titles()
    {
        var net = new TrayToast("Internet healthy again", "Internet: healthy", ToastLevel.Info);
        var t = TrayToasts.Combine(net, [Back("Router")])!;
        Assert.Equal("Router is answering again", t.Title); // critical recovery outranks internet info
        Assert.Contains("Internet healthy again", t.Body);
        Assert.Equal(ToastLevel.Info, t.Level);
    }

    [Fact]
    public void A_critical_failure_outranks_an_internet_warning_and_the_body_keeps_both()
    {
        var net = new TrayToast("Internet degraded", "slow", ToastLevel.Warning);
        var t = TrayToasts.Combine(net, [Fail("Router")])!;
        Assert.Equal("Router is not answering", t.Title);
        Assert.Contains("Internet degraded", t.Body);
        Assert.Equal(ToastLevel.Warning, t.Level);
    }

    [Fact]
    public void An_internet_error_outranks_a_critical_recovery_and_levels_and_notes_combine()
    {
        var net = new TrayToast("Internet DOWN", "d", ToastLevel.Error);
        var t = TrayToasts.Combine(net, [Back("Router"), Fail("NAS", notes: true)])!;
        Assert.Equal("NAS is not answering", t.Title); // critical failure first
        Assert.Equal(ToastLevel.Error, t.Level);
        Assert.True(t.OpenNotes);
        var t2 = TrayToasts.Combine(net, [Back("Router")])!;
        Assert.Equal("Internet DOWN", t2.Title);
        Assert.Contains("Router is answering again", t2.Body);
        Assert.False(t2.OpenNotes);
    }

    private static TrayInfo Info(TrayState state, string? verdict = null, string? detail = null) =>
        new(state, detail, []) { Verdict = verdict };

    [Fact]
    public void Entering_DnsDown_says_name_lookups_are_failing_with_the_blame()
    {
        var toast = TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"),
            Info(TrayState.Down, "DnsDown", "DnsDown: Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly."), "http://localhost:8080")!;
        Assert.Equal("Name lookups are failing", toast.Title);
        Assert.Equal("Your internet is up, but name lookups are failing — Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", toast.Body);
        Assert.Equal(ToastLevel.Error, toast.Level);
    }

    [Fact]
    public void DnsDown_and_LinkDown_toast_each_other_even_though_both_are_Down() // Review Focus 4
    {
        var dns = Info(TrayState.Down, "DnsDown", "DnsDown: No resolver is answering, though the internet is reachable.");
        var link = Info(TrayState.Down, "LinkDown", "LinkDown: Modem is Down");
        Assert.Equal("Internet DOWN", TrayToasts.Internet(dns, link, "u")!.Title);
        Assert.Equal("LinkDown: Modem is Down", TrayToasts.Internet(dns, link, "u")!.Body);
        Assert.Equal("Name lookups are failing", TrayToasts.Internet(link, dns, "u")!.Title);
        Assert.Null(TrayToasts.Internet(dns, dns with { Detail = "DnsDown: changed wording" }, "u")); // same verdict: no toast
    }

    [Fact]
    public void Leaving_DnsDown_for_healthy_toasts_the_recovery()
    {
        var toast = TrayToasts.Internet(Info(TrayState.Down, "DnsDown", "d"), Info(TrayState.Healthy, "Healthy", "Healthy: 2 anchors responding"), "u")!;
        Assert.Equal("Internet healthy again", toast.Title);
        Assert.Equal("Name lookups are working again.", toast.Body);
        Assert.Equal(ToastLevel.Info, toast.Level);
    }

    [Fact]
    public void The_old_toasts_are_unchanged_and_measuring_never_toasts()
    {
        Assert.Equal(("Internet DOWN", ToastLevel.Error), Pick(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Down, "LinkDown", "LinkDown: Modem is Down"), "u")));
        Assert.Equal(("Internet degraded", ToastLevel.Warning), Pick(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Degraded, "LinkDegraded", "d"), "u")));
        Assert.Equal(("Internet healthy again", ToastLevel.Info), Pick(TrayToasts.Internet(Info(TrayState.Down, "LinkDown"), Info(TrayState.Healthy, "Healthy", "h"), "u")));
        var unreachable = TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Unreachable), "http://nas:8080")!;
        Assert.Equal("ConnectionWatcher unreachable", unreachable.Title);
        Assert.Equal("The monitor at http://nas:8080 is not answering.", unreachable.Body);
        Assert.Null(TrayToasts.Internet(Info(TrayState.Measuring), Info(TrayState.Healthy, "Healthy"), "u"));
        Assert.Null(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Measuring), "u"));
        Assert.Null(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Healthy, "SiteIncident", "s"), "u")); // still healthy: no toast
    }

    private static (string, ToastLevel) Pick(TrayToast? t) => (t!.Title, t.Level);
}
