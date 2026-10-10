using ConInfrastructure.Tray;

namespace ConWatch.Tests;

public class CriticalWatchTests
{
    private static TrayInfo Info(TrayState state, params TrayCritical[] critical) =>
        new(state, null, []) { Critical = critical };

    private static TrayCritical Router(string state, bool bypassed = false) =>
        new("r", "Router", state, bypassed);

    [Fact]
    public void A_critical_failure_toasts_once_and_its_recovery_once()
    {
        var watch = new CriticalWatch();
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));

        var down = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true));
        Assert.Equal("Router is not answering", down.Title);
        Assert.Equal("Your internet is still up.", down.Body);
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Unreachable")), announce: true)); // reclassified

        var back = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
        Assert.Equal("Router is answering again", back.Title);
    }

    [Fact]
    public void A_bypassed_critical_hop_says_traffic_is_still_passing_it()
    {
        var watch = new CriticalWatch();
        var toast = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Down", bypassed: true)), announce: true));
        Assert.Equal("Router is not answering",
            toast.Title);
        Assert.Equal("Traffic is still passing it, so your internet is up — see ConnectionWatcher notes.", toast.Body);
    }

    [Fact]
    public void A_released_target_recovers_through_starting_with_one_toast()
    {
        var watch = new CriticalWatch();
        watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true);
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Starting")), announce: true));
        Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void An_unreachable_service_is_no_news_either_way()
    {
        var watch = new CriticalWatch();
        watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true);
        Assert.Empty(watch.Update(new TrayInfo(TrayState.Unreachable, null, []), announce: true));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true)); // still announced
    }

    [Fact]
    public void A_target_deleted_or_unmarked_while_down_is_dropped_silently()
    {
        var watch = new CriticalWatch();
        watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true);
        Assert.Empty(watch.Update(Info(TrayState.Healthy), announce: true));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void A_critical_failure_during_an_internet_outage_still_toasts_and_points_at_the_device()
    {
        // In an ISP outage the router usually still answers; one that does not
        // is its own signal: check the equipment (Mark, 2026-10-10).
        var watch = new CriticalWatch();
        var toast = Assert.Single(watch.Update(Info(TrayState.Down, Router("Down")), announce: true));
        Assert.Equal("Router is not answering", toast.Title);
        Assert.Equal("The internet is down too — check Router itself.", toast.Body);
        Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void A_router_that_answers_through_an_isp_outage_never_toasts()
    {
        var watch = new CriticalWatch();
        Assert.Empty(watch.Update(Info(TrayState.Down, Router("Responding")), announce: true));
    }

    [Fact]
    public void A_silent_update_records_state_without_toasting()
    {
        // First poll after the tray starts: no toast for what is already down,
        // and no repeat later; its recovery still toasts.
        var watch = new CriticalWatch();
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: false));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true));
        Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void While_degraded_the_body_does_not_claim_the_internet_is_fine()
    {
        var watch = new CriticalWatch();
        var toast = Assert.Single(watch.Update(Info(TrayState.Degraded, Router("Down")), announce: true));
        Assert.Equal("Your internet is degraded too.", toast.Body);
        Assert.Equal(ToastLevel.Warning, toast.Level);
    }

    [Fact]
    public void Recoveries_are_info_and_bypassed_failures_open_the_notes()
    {
        var watch = new CriticalWatch();
        var down = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Down", bypassed: true)), announce: true));
        Assert.Equal(ToastLevel.Warning, down.Level);
        Assert.True(down.OpenNotes);
        var back = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
        Assert.Equal(ToastLevel.Info, back.Level);
        Assert.False(back.OpenNotes);
    }
}
