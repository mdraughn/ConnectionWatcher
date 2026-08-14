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
    public async Task Initial_delay_applies_once()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time, initialDelay: TimeSpan.FromSeconds(3));
        d.OnStateChanged(HostState.Responding);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(3)));

        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(3)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(7)));
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
