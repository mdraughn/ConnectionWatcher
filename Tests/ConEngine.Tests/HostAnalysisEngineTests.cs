using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
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
    public void An_explanation_alone_does_not_make_a_healthy_host_unreachable()
    {
        // Spec 2026-10-09 D4: never infer Unreachable; own evidence first.
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var count = transitions.Count;

        tracker.SetExplanation(Guid.NewGuid());
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        Assert.Null(tracker.ExplainedBy);
        Assert.Equal(count, transitions.Count);

        Feed(tracker, time, 5, success: true); // still answering: still Responding
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void Own_down_with_an_explanation_is_unreachable_naming_the_root_cause()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var root = Guid.NewGuid();
        tracker.SetExplanation(root);

        Feed(tracker, time, 20, success: false); // Responding -> Faltering -> (Down, explained)
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        Assert.Equal(root, tracker.ExplainedBy);
        var t = transitions.Last();
        Assert.Equal(HostState.Unreachable, t.To);
        Assert.Equal(TransitionTrigger.ParentDown, t.Reason.Trigger);
        Assert.Equal(root, t.Reason.AncestorId);
        Assert.DoesNotContain(transitions, x => x.To == HostState.Down); // never shown Down first
    }

    [Fact]
    public void Explaining_an_already_down_host_turns_it_unreachable()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Down, tracker.CurrentState);

        var root = Guid.NewGuid();
        tracker.SetExplanation(root);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        Assert.Equal((HostState.Down, HostState.Unreachable), (transitions.Last().From, transitions.Last().To));
        Assert.Equal(root, transitions.Last().Reason.AncestorId);
        Assert.Equal(0.0, transitions.Last().Reason.SuccessRate!.Value, 3); // the evidence behind the own Down
        Assert.Equal(5, transitions.Last().Reason.SampleSize);
    }

    [Fact]
    public void Withdrawing_the_explanation_releases_the_host_to_reprove_on_its_own_evidence()
    {
        // Final review I1: an explanation withdrawn while the cause is still
        // Down (proof of forwarding appeared) must not raise "no longer
        // explained" Down alarms; the host re-proves from Starting instead.
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var root = Guid.NewGuid();
        tracker.SetExplanation(root);
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);

        tracker.SetExplanation(null);
        Assert.Equal(HostState.Starting, tracker.CurrentState);
        Assert.Equal(0, tracker.HistoryCount);
        var t = transitions.Last();
        Assert.Equal((HostState.Unreachable, HostState.Starting), (t.From, t.To));
        Assert.Equal(TransitionTrigger.ParentRecovered, t.Reason.Trigger);
        Assert.Equal(root, t.Reason.AncestorId);
        Assert.DoesNotContain(transitions, x => x.To == HostState.Down);

        // Still down on its own: re-proves Down, stamped with the former cause.
        Feed(tracker, time, 5, success: false);
        var own = transitions.Last();
        Assert.Equal((HostState.Starting, HostState.Down), (own.From, own.To));
        Assert.Equal(TransitionTrigger.ThresholdCrossed, own.Reason.Trigger);
        Assert.Equal(root, own.Reason.AncestorId);
    }

    [Fact]
    public void Only_the_first_transition_out_of_a_released_starting_carries_the_former_cause()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var root = Guid.NewGuid();
        tracker.SetExplanation(root);
        Feed(tracker, time, 20, success: false);
        tracker.Release();

        Feed(tracker, time, 5, success: true);
        var first = transitions.Last();
        Assert.Equal((HostState.Starting, HostState.Responding), (first.From, first.To));
        Assert.Equal(TransitionTrigger.ThresholdCrossed, first.Reason.Trigger);
        Assert.Equal(root, first.Reason.AncestorId);

        Feed(tracker, time, 2, success: false); // Responding -> Faltering: its own business
        Assert.Equal(HostState.Faltering, transitions.Last().To);
        Assert.Null(transitions.Last().Reason.AncestorId);
    }

    [Fact]
    public void A_new_explanation_forgets_the_cause_a_release_came_from()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.SetExplanation(Guid.NewGuid());
        Feed(tracker, time, 20, success: false);
        tracker.Release();

        tracker.SetExplanation(Guid.NewGuid()); // a cause fails again before this re-proved
        Feed(tracker, time, 5, success: true);
        Assert.Equal((HostState.Starting, HostState.Responding), (transitions.Last().From, transitions.Last().To));
        Assert.Null(transitions.Last().Reason.AncestorId);
    }

    [Fact]
    public void An_unreachable_host_that_answers_again_recovers_on_its_own_evidence()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.SetExplanation(Guid.NewGuid());
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);

        Feed(tracker, time, 3, success: true); // 3 of 5 >= RecoveringThreshold
        Assert.Equal(HostState.Recovering, tracker.CurrentState);
        Assert.Equal((HostState.Unreachable, HostState.Recovering), (transitions.Last().From, transitions.Last().To));
    }

    [Fact]
    public void Release_returns_an_unreachable_host_to_starting_with_fresh_history()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.SetExplanation(Guid.NewGuid());
        Feed(tracker, time, 20, success: false);

        tracker.Release();
        Assert.Equal(HostState.Starting, tracker.CurrentState);
        Assert.Equal(0, tracker.HistoryCount);
        Assert.Null(tracker.ExplainedBy);
        Assert.Equal(TransitionTrigger.ParentRecovered, transitions.Last().Reason.Trigger);

        Feed(tracker, time, 5, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void Release_of_a_host_that_is_not_unreachable_keeps_its_history()
    {
        // Final review I4: the history is cleared only when the engine releases.
        var (tracker, _, time) = Make();
        Feed(tracker, time, 5, success: true);
        Assert.Equal(5, tracker.HistoryCount);

        tracker.Release();
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        Assert.Equal(5, tracker.HistoryCount);
    }

    [Fact]
    public void Release_and_repeated_explanations_are_idempotent()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.Release();                      // not Unreachable: no-op
        var root = Guid.NewGuid();
        tracker.SetExplanation(root);
        tracker.SetExplanation(root);           // same: no-op
        Feed(tracker, time, 20, success: false);
        var count = transitions.Count;
        tracker.SetExplanation(Guid.NewGuid()); // another cause, still Unreachable: silent
        Assert.Equal(count, transitions.Count);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
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
