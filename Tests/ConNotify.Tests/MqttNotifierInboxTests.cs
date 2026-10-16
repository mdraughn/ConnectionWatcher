using ConEngine.Events;
using ConEngine.Tracking;
using ConNotify;

namespace ConNotify.Tests;

public class MqttNotifierInboxTests
{
    private static TargetTransition Transition(string name) => new()
    {
        TargetId = Guid.NewGuid(), TargetName = name,
        From = HostState.Responding, To = HostState.Down,
        Timestamp = DateTimeOffset.UtcNow,
        Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed },
    };

    [Fact]
    public async Task One_reconfigure_after_traffic_is_read_after_that_traffic()
    {
        var inbox = new MqttNotifier.Inbox();
        inbox.Post(Transition("A"));
        inbox.Post(Transition("B"));
        inbox.Post(Transition("C"));
        inbox.Reconfigure();

        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<MqttNotifier.Inbox.ReconfigureMessage>(await inbox.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Waiting_for_a_reconfigure_skips_traffic_and_returns_on_the_poke()
    {
        var inbox = new MqttNotifier.Inbox();
        inbox.Post(Transition("A"));
        inbox.Refresh();
        var waiting = inbox.WaitForReconfigureAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        inbox.Reconfigure();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        // Nothing is left behind for the next session to trip over.
        inbox.Post(Transition("D"));
        var next = await inbox.ReadAsync(CancellationToken.None);
        Assert.Equal("D", Assert.IsType<TargetTransition>(next).TargetName);
    }

    [Fact]
    public async Task A_tick_is_delivered_in_order_with_traffic()
    {
        var inbox = new MqttNotifier.Inbox();
        inbox.Post(Transition("A"));
        inbox.Tick();
        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<MqttNotifier.Inbox.TickMessage>(await inbox.ReadAsync(CancellationToken.None));
    }
}
