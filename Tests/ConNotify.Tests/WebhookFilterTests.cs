using ConEngine.Events;
using ConEngine.Tracking;
using ConNotify;

namespace ConNotify.Tests;

public class WebhookFilterTests
{
    private static TargetTransition T(HostState from, HostState to,
        TransitionSubjectKind kind = TransitionSubjectKind.Target, TransitionTrigger trigger = TransitionTrigger.ThresholdCrossed) => new()
    {
        Kind = kind, TargetId = Guid.NewGuid(), TargetName = "Google", From = from, To = to,
        Timestamp = DateTimeOffset.Parse("2026-10-09T00:00:00Z"), Reason = new TransitionReason { Trigger = trigger },
    };

    [Fact]
    public void An_explained_failure_and_its_recovery_are_skipped_by_default()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), false, true));
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Starting, trigger: TransitionTrigger.ParentRecovered), false, true));
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Recovering), false, true));
    }

    [Fact]
    public void A_target_no_longer_explained_is_its_own_problem_and_is_delivered()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Down), false, true));
    }

    [Fact]
    public void The_root_cause_and_ordinary_transitions_are_delivered()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Down), false, true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Down, HostState.Recovering), false, true));
    }

    [Fact]
    public void With_skipping_off_everything_is_delivered()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), false, false));
    }

    [Fact]
    public void Verdict_only_still_drops_targets_and_keeps_verdicts()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Faltering), true, false));
        Assert.True(WebhookFilter.ShouldDeliver(
            T(HostState.Responding, HostState.Down, TransitionSubjectKind.Verdict, TransitionTrigger.VerdictChanged), true, true));
    }
}
