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
}
