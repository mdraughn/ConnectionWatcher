using ConCore.Targets;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class PingDisciplineTests
{
    private static readonly PingProfile Profile = new()
    {
        Name = "Test",
        NormalInterval = TimeSpan.FromSeconds(10),
        BurstInterval = TimeSpan.FromSeconds(1),
        BurstMaxDuration = TimeSpan.FromSeconds(60),
    };

    // Fake time drives the discipline, but its continuations still need real
    // time to run: a completion gets a generous real window (only spent when
    // something is broken), a "stays pending" check a short fixed one.
    private static async Task<bool> Completes(Task task, FakeTimeProvider time, TimeSpan advance)
    {
        time.Advance(advance);
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10)));
        return winner == task;
    }

    private static async Task<bool> StaysPending(Task task, FakeTimeProvider time, TimeSpan advance)
    {
        time.Advance(advance);
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        return winner != task;
    }

    [Fact]
    public async Task Normal_state_uses_normal_interval()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await StaysPending(wait, time, TimeSpan.FromSeconds(9)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Faltering_uses_burst_interval()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Faltering);
        Assert.NotNull(d.BurstStartedAt);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Down_burst_expires_after_cap()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Down);

        // Within the 60s window: burst cadence.
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));

        // Push past the cap: back to normal cadence.
        time.Advance(TimeSpan.FromSeconds(70));
        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await StaysPending(wait, time, TimeSpan.FromSeconds(1)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public async Task Unreachable_keeps_probing_at_burst_cadence()
    {
        // Spec 2026-10-09 D4: a target is never parked; Unreachable is a
        // failing state like Down and keeps proving itself.
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Unreachable);
        Assert.NotNull(d.BurstStartedAt);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ForceBurst_ignores_the_suspicion_guard()
    {
        var time = new FakeTimeProvider();
        var shortBurst = Profile with { BurstMaxDuration = TimeSpan.FromSeconds(10) };
        var d = new PingDiscipline(shortBurst, time);
        d.OnStateChanged(HostState.Responding);

        d.OnPingResult(success: false);       // suspicion burst starts the 60 s guard
        time.Advance(TimeSpan.FromSeconds(30)); // burst over, guard still active
        d.TriggerBurst();
        Assert.False(d.IsBurstActive);         // the guard refuses a polite burst…

        d.ForceBurst();
        Assert.True(d.IsBurstActive);          // …but topology's "reach beyond now" is not polite
    }

    [Fact]
    public void ForceBurst_never_extends_an_active_burst()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);
        d.ForceBurst();
        var started = d.BurstStartedAt;

        time.Advance(TimeSpan.FromSeconds(30));
        d.ForceBurst();
        Assert.Equal(started, d.BurstStartedAt);
    }

    [Fact]
    public async Task Phase_offset_delays_the_first_ping_and_anchors_the_grid()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time, phaseOffset: TimeSpan.FromSeconds(3));
        d.OnStateChanged(HostState.Responding);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(3)));

        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await StaysPending(wait, time, TimeSpan.FromSeconds(3)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public async Task Normal_cadence_returns_to_the_phase_slot_after_a_burst()
    {
        var time = new FakeTimeProvider();
        // Pool member 1 of 4: slots at 2.5, 12.5, 22.5, ...
        var d = new PingDiscipline(Profile, time, phaseOffset: TimeSpan.FromSeconds(2.5));
        d.OnStateChanged(HostState.Responding);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(2.5))); // t=2.5, on slot

        // A corroboration sweep bursts the member off its phase.
        d.TriggerBurst();
        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));   // t=3.5, off slot

        // Burst window expires; the next normal wait must re-align to the
        // slot grid, not run a flat interval from the last burst ping —
        // otherwise one sweep collapses the whole pool into lockstep.
        time.Advance(TimeSpan.FromSeconds(62));                              // t=65.5, calm again
        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await StaysPending(wait, time, TimeSpan.FromSeconds(6.5))); // t=72, not yet
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(0.5)));  // t=72.5, on slot
    }

    [Fact]
    public async Task Late_wakeup_fires_the_owed_ping_immediately_instead_of_skipping_the_slot()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        // First ping due at t=10; the loop wakes LATE, at t=25 (load spike).
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(25))); // t=25

        // The t=20 slot was owed: the next wait must fire immediately, not
        // wait for t=30 — lateness may never cost sample rate.
        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.Zero));

        // And the one after glides back onto the grid: due t=30, 5s away.
        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await StaysPending(wait, time, TimeSpan.FromSeconds(4))); // t=29
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));  // t=30
    }

    [Fact]
    public async Task TriggerBurst_wakes_a_pending_normal_wait()
    {
        var time = new TimerCountingTime();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);
        var first = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(first, time, TimeSpan.FromSeconds(10))); // t=10, on slot

        // A corroboration sweep lands while the controller sleeps toward t=20.
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await StaysPending(wait, time, TimeSpan.Zero));
        var timers = time.TimersCreated;
        d.TriggerBurst();
        await time.NewTimerArmedSince(timers); // the woken loop armed its burst timer
        // One burst interval later the ping must fire — not at t=20.
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));  // t=11
    }

    [Fact]
    public async Task Lost_ping_wakes_a_pending_normal_wait()
    {
        var time = new TimerCountingTime();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);
        var first = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(first, time, TimeSpan.FromSeconds(10)));

        // The controller computed its next normal delay before the manager
        // thread delivered the loss (the usual ordering): the suspicion burst
        // must still start now, not after the 10 s sleep.
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await StaysPending(wait, time, TimeSpan.Zero));
        var timers = time.TimersCreated;
        d.OnPingResult(success: false);
        await time.NewTimerArmedSince(timers); // the woken loop armed its burst timer
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Failed_ping_while_responding_starts_suspicion_burst()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        d.OnPingResult(success: false);
        Assert.True(d.IsBurstActive);

        // Burst cadence applies even though the state is still Responding.
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Success_never_starts_or_extends_a_burst()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        d.OnPingResult(success: true);
        Assert.False(d.IsBurstActive);

        d.OnPingResult(success: false);
        var started = d.BurstStartedAt;
        time.Advance(TimeSpan.FromSeconds(5));
        d.OnPingResult(success: true);
        Assert.Equal(started, d.BurstStartedAt);
    }

    [Fact]
    public void Suspicion_burst_respects_politeness_guard()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        d.OnPingResult(success: false);
        var started = d.BurstStartedAt;

        // Mid-burst failure: no restart (window would extend forever on a lossy line).
        time.Advance(TimeSpan.FromSeconds(30));
        d.OnPingResult(success: false);
        Assert.Equal(started, d.BurstStartedAt);

        // Window expired but <60s since it STARTED at this point? 30+31=61s > 60s guard,
        // and the window (60s cap) has expired -> a fresh failure starts a new burst.
        time.Advance(TimeSpan.FromSeconds(31));
        d.OnPingResult(success: false);
        Assert.NotEqual(started, d.BurstStartedAt);
        Assert.True(d.IsBurstActive);
    }

    [Fact]
    public void Suspicion_burst_blocked_within_60s_of_last_burst_start()
    {
        var time = new FakeTimeProvider();
        var shortBurst = Profile with { BurstMaxDuration = TimeSpan.FromSeconds(10) };
        var d = new PingDiscipline(shortBurst, time);
        d.OnStateChanged(HostState.Responding);

        d.OnPingResult(success: false);
        time.Advance(TimeSpan.FromSeconds(30)); // window (10s) long expired, but <60s since start
        d.OnPingResult(success: false);
        Assert.False(d.IsBurstActive); // guard blocked the restart
    }

    [Fact]
    public async Task TriggerBurst_starts_burst_without_state_change()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        d.TriggerBurst();
        Assert.True(d.IsBurstActive);
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TriggerBurst_works_while_unreachable()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Unreachable);
        time.Advance(TimeSpan.FromSeconds(120)); // the state-change burst and the guard have expired
        d.TriggerBurst();
        Assert.True(d.IsBurstActive);
    }

    [Fact]
    public async Task Reentering_down_restarts_burst_window()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Down);
        time.Advance(TimeSpan.FromSeconds(70)); // burst expired
        d.OnStateChanged(HostState.Recovering); // window restarts
        d.OnStateChanged(HostState.Down);       // window restarts again

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    // Spec 2026-08-14: a target that is already failing must not start new
    // bursts from its own losses, or a long outage hammers a public anchor
    // forever. The state-change burst is the only one a failing state gets.
    [Theory]
    [InlineData(HostState.Down)]
    [InlineData(HostState.Faltering)]
    [InlineData(HostState.Recovering)]
    [InlineData(HostState.Unreachable)]
    public async Task A_loss_in_a_failing_state_never_starts_a_burst(HostState failing)
    {
        var time = new TimerCountingTime();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(failing);
        time.Advance(TimeSpan.FromSeconds(120)); // the 60 s cap and the 60 s guard are both over

        d.OnPingResult(success: false);
        Assert.False(d.IsBurstActive);

        var timers = time.TimersCreated;
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        await time.NewTimerArmedSince(timers);
        Assert.True(await StaysPending(wait, time, TimeSpan.FromSeconds(9))); // not a burst-interval wait
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));    // done only after NormalInterval
    }

    [Fact]
    public async Task Entering_responding_ends_an_active_burst()
    {
        var time = new TimerCountingTime();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Down);
        Assert.NotNull(d.BurstStartedAt);

        d.OnStateChanged(HostState.Responding);
        Assert.Null(d.BurstStartedAt);
        Assert.False(d.IsBurstActive);

        var timers = time.TimersCreated;
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        await time.NewTimerArmedSince(timers);
        Assert.True(await StaysPending(wait, time, TimeSpan.FromSeconds(9)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_loss_while_starting_starts_a_burst()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Starting);

        d.OnPingResult(success: false);
        Assert.True(d.IsBurstActive);
        Assert.NotNull(d.BurstStartedAt);
    }
}
