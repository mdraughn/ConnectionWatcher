using ConEngine.Topology;

namespace ConEngine.Tests;

public class LossWindowsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-10T12:00:00Z"); // a window start
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static void Fill(LossWindows w, Guid id, DateTimeOffset start, int samples, int lost)
    {
        for (int i = 0; i < samples; i++)
            w.Record(id, start + TimeSpan.FromSeconds(i * 50.0 / samples), success: i >= lost);
    }

    [Fact]
    public void Window_start_is_the_utc_minute()
    {
        Assert.Equal(T0, LossWindows.WindowStart(T0.AddSeconds(59.9)));
        Assert.Equal(T0.AddMinutes(1), LossWindows.WindowStart(T0.AddSeconds(60)));
    }

    [Fact]
    public void A_window_closes_once_a_sample_passes_its_end_plus_the_close_delay()
    {
        var w = new LossWindows();
        var closed = new List<LossWindow>();
        w.WindowClosed.Subscribe(closed.Add);
        Fill(w, A, T0, samples: 10, lost: 3);
        Fill(w, B, T0, samples: 6, lost: 0);

        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay - TimeSpan.FromMilliseconds(1), true);
        Assert.Empty(w.Closed); // still within the close delay

        w.Record(B, T0 + LossWindows.Span + LossWindows.CloseDelay, true);
        var window = Assert.Single(w.Closed);
        Assert.Same(window, Assert.Single(closed));
        Assert.Equal(T0, window.Start);
        Assert.Equal(new WindowCount(10, 3), window.Targets[A]);
        Assert.Equal(0.3, window.Targets[A].Loss, 3);
        Assert.True(window.Targets[B].Usable);
    }

    [Fact]
    public void Late_samples_for_a_closed_window_are_dropped_not_counted_in_the_next()
    {
        var w = new LossWindows();
        Fill(w, A, T0, 10, 0);
        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay, true); // closes T0
        w.Record(B, T0 + TimeSpan.FromSeconds(30), false);                  // late, for T0

        Assert.False(w.Closed[0].Targets.ContainsKey(B));
        Assert.Null(w.Open(B)); // and not in the open window either
    }

    [Fact]
    public void Usable_needs_the_minimum_samples_and_answered_needs_one_reply()
    {
        Assert.False(new WindowCount(LossWindows.MinSamples - 1, 0).Usable);
        Assert.True(new WindowCount(LossWindows.MinSamples, 0).Usable);
        Assert.False(new WindowCount(6, 6).Answered);
        Assert.True(new WindowCount(6, 5).Answered);
    }

    [Fact]
    public void Keeps_at_most_Keep_windows_oldest_first()
    {
        var w = new LossWindows();
        for (int i = 0; i <= LossWindows.Keep + 2; i++)
            Fill(w, A, T0.AddMinutes(i), 6, 0);
        w.Record(A, T0.AddMinutes(LossWindows.Keep + 4), true);

        Assert.Equal(LossWindows.Keep, w.Closed.Count);
        Assert.True(w.Closed[0].Start < w.Closed[^1].Start);
        Assert.Equal(T0.AddMinutes(LossWindows.Keep + 2), w.Closed[^1].Start);
    }

    [Fact]
    public void LatestFresh_ignores_a_stale_window()
    {
        var w = new LossWindows();
        Fill(w, A, T0, 6, 0);
        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay, true);
        var end = T0 + LossWindows.Span;

        Assert.NotNull(w.LatestFresh(end + LossWindows.Span * 2));
        Assert.Null(w.LatestFresh(end + LossWindows.Span * 2 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Open_reports_the_newest_open_windows_count()
    {
        var w = new LossWindows();
        Assert.Null(w.Open(A));
        Fill(w, A, T0, 3, 1);
        Assert.Equal(new WindowCount(3, 1), w.Open(A));
    }

    [Fact]
    public void Window_closed_is_published_outside_the_lock()
    {
        // A subscriber reading Closed from another thread inside OnNext would
        // deadlock if publication happened under the collector's lock.
        var w = new LossWindows();
        var reads = new List<int>();
        w.WindowClosed.Subscribe(_ =>
            reads.Add(Task.Run(() => w.Closed.Count).Wait(TimeSpan.FromSeconds(10)) ? 1 : 0));
        Fill(w, A, T0, 6, 0);
        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay, true);
        Assert.Equal([1], reads);
    }
}
