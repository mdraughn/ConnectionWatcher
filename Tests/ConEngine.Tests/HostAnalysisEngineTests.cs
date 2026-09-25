using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class HostAnalysisEngineTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("10.0.0.1");
    private static readonly PingProfile Profile = new()
    {
        Name = "Test",
        HistorySize = 20,
        MinUsableSample = 5,
        RespondingThreshold = 0.95,
        FalteringThreshold = 0.75,
        DownThreshold = 0.10,
        RecoveringThreshold = 0.25,
    };

    private static (TrackerProbe tracker, List<AnalysisTransition> transitions, FakeTimeProvider time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var tracker = new TrackerProbe(Ip, Profile, time);
        return (tracker, tracker.Transitions, time);
    }

    private static void Feed(TrackerProbe tracker, FakeTimeProvider time, int count, bool success)
    {
        for (int i = 0; i < count; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            tracker.RecordPingResult(new PingResult(Ip, success, success ? 10 : 0,
                timestamp: time.GetUtcNow()));
        }
    }

    [Fact]
    public void Starting_exits_to_responding_on_good_sample()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        var t = transitions.Last();
        Assert.Equal(HostState.Starting, t.From);
        Assert.Equal(TransitionTrigger.ThresholdCrossed, t.Reason.Trigger);
        Assert.Equal(1.0, t.Reason.SuccessRate!.Value, 3);
        Assert.Equal(5, t.Reason.SampleSize);
    }

    [Fact]
    public void Starting_exits_to_down_when_host_never_answers()
    {
        var (tracker, _, time) = Make();
        Feed(tracker, time, 5, success: false);
        Assert.Equal(HostState.Down, tracker.CurrentState);
    }

    [Fact]
    public void Starting_exits_to_faltering_on_middling_sample()
    {
        var (tracker, _, time) = Make();
        // 3 of 5 = 60%: below Responding (95%), above Down (10%)
        Feed(tracker, time, 3, success: true);
        Feed(tracker, time, 2, success: false);
        Assert.Equal(HostState.Faltering, tracker.CurrentState);
    }

    [Fact]
    public void Responding_falls_to_faltering_then_down()
    {
        var (tracker, _, time) = Make();
        Feed(tracker, time, 20, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Down, tracker.CurrentState);
    }

    [Fact]
    public void MarkUnreachable_parks_and_ignores_stale_results()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var ancestor = Guid.NewGuid();

        tracker.MarkUnreachable(ancestor);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        var t = transitions.Last();
        Assert.Equal(TransitionTrigger.ParentDown, t.Reason.Trigger);
        Assert.Equal(ancestor, t.Reason.AncestorId);

        // A stale in-flight result must not move the state.
        Feed(tracker, time, 1, success: false);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
    }

    [Fact]
    public void Release_returns_to_starting_with_fresh_history()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.MarkUnreachable(Guid.NewGuid());

        tracker.Release();
        Assert.Equal(HostState.Starting, tracker.CurrentState);
        Assert.Equal(0, tracker.HistoryCount);
        Assert.Equal(TransitionTrigger.ParentRecovered, transitions.Last().Reason.Trigger);

        // Must re-prove itself: 5 good pings to get back to Responding.
        Feed(tracker, time, 5, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void MarkUnreachable_is_idempotent()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.MarkUnreachable(Guid.NewGuid());
        var countAfterFirst = transitions.Count;
        tracker.MarkUnreachable(Guid.NewGuid());
        Assert.Equal(countAfterFirst, transitions.Count);
    }

    [Fact]
    public void EntriesSince_returns_only_entries_in_window()
    {
        var (tracker, _, time) = Make();
        Feed(tracker, time, 5, success: true);
        var cutoff = time.GetUtcNow();
        Feed(tracker, time, 3, success: false);
        Assert.Equal(3, tracker.EntriesSince(cutoff.AddTicks(1)).Count());
    }
}
