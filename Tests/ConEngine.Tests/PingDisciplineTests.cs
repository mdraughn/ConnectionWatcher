using ConCore.Targets;
using ConEngine.Scheduling;
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

    private static async Task<bool> Completes(Task task, FakeTimeProvider time, TimeSpan advance)
    {
        time.Advance(advance);
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        return winner == task;
    }

    [Fact]
    public async Task Normal_state_uses_normal_interval()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(9)));
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
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(1)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public async Task Unreachable_parks_until_state_change()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Unreachable);
        Assert.True(d.IsParked);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.False(await Completes(wait, time, TimeSpan.FromMinutes(30)));

        d.OnStateChanged(HostState.Starting);
        Assert.False(d.IsParked);
        await Task.Delay(100); // let the unparked waiter reach its next fake-time timer
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(10)));
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
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(3)));
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
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(6.5))); // t=72, not yet
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
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(4))); // t=29
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));  // t=30
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
    public void TriggerBurst_is_ignored_while_parked()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Unreachable);
        d.TriggerBurst();
        Assert.False(d.IsBurstActive);
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
}
