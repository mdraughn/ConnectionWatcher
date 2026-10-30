using System.Collections.Concurrent;
using ConEngine.Events;

namespace ConEngine.Tests;

public class OrderedOutboxTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Items_are_published_in_enqueue_order()
    {
        var seen = new List<int>();
        var box = new OrderedOutbox<int>(seen.Add, OutboxMode.Skip);
        box.Enqueue(1); box.Enqueue(2); box.Drain();
        Assert.Equal([1, 2], seen);
    }

    [Theory]
    [InlineData(OutboxMode.Wait)]
    [InlineData(OutboxMode.Skip)]
    public async Task An_item_queued_while_another_thread_publishes_is_delivered_after_it(OutboxMode mode)
    {
        using var inFirst = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var seen = new ConcurrentQueue<int>();
        var box = new OrderedOutbox<int>(i => { if (i == 1) { inFirst.Set(); release.Wait(Timeout); } seen.Enqueue(i); }, mode);

        box.Enqueue(1);
        var first = Task.Run(box.Drain);
        Assert.True(inFirst.Wait(Timeout));
        box.Enqueue(2);
        var second = Task.Run(() => { box.Drain(); return seen.Contains(2); });

        if (mode == OutboxMode.Skip)
            Assert.False(await second.WaitAsync(Timeout)); // returned at once; the first drainer owns item 2
        release.Set();
        await first.WaitAsync(Timeout);
        if (mode == OutboxMode.Wait)
            Assert.True(await second.WaitAsync(Timeout)); // returned only after item 2 was out
        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void A_throwing_publish_leaves_later_items_for_the_next_drain()
    {
        var seen = new List<int>();
        var box = new OrderedOutbox<int>(i => { if (i == 1) throw new InvalidOperationException(); seen.Add(i); }, OutboxMode.Skip);
        box.Enqueue(1); box.Enqueue(2);
        Assert.Throws<InvalidOperationException>(box.Drain);
        box.Drain();
        Assert.Equal([2], seen);
    }
}
