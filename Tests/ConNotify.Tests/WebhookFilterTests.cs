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
    public void A_released_targets_recovery_naming_its_former_cause_is_part_of_the_incident()
    {
        var cause = Guid.NewGuid();
        var back = T(HostState.Starting, HostState.Responding) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, AncestorId = cause },
        };
        Assert.False(WebhookFilter.ShouldDeliver(back, false, true));
        Assert.False(WebhookFilter.ShouldDeliver(back with { To = HostState.Recovering }, false, true));
        Assert.True(WebhookFilter.ShouldDeliver(back, false, false)); // skipping off
    }

    [Fact]
    public void A_released_target_still_down_after_its_cause_recovered_is_delivered()
    {
        var stillDown = T(HostState.Starting, HostState.Down) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, AncestorId = Guid.NewGuid() },
        };
        Assert.True(WebhookFilter.ShouldDeliver(stillDown, false, true));
        // A plain first verdict (no former cause) is delivered as before.
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Starting, HostState.Responding), false, true));
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

    [Fact]
    public void A_critical_targets_failure_and_recovery_pass_verdict_only_and_skip_explained()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Down, HostState.Recovering), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Responding), true, true, critical: true));
    }

    [Fact]
    public void A_released_critical_targets_recovery_is_delivered_once_and_the_release_itself_is_not()
    {
        var cause = Guid.NewGuid();
        var release = T(HostState.Unreachable, HostState.Starting, trigger: TransitionTrigger.ParentRecovered);
        var back = T(HostState.Starting, HostState.Responding) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, AncestorId = cause },
        };
        Assert.False(WebhookFilter.ShouldDeliver(release, true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(back, true, true, critical: true));
    }

    [Fact]
    public void Reclassifying_a_critical_target_between_down_and_unreachable_is_not_a_new_event()
    {
        Assert.False(WebhookFilter.IsCriticalEvent(T(HostState.Down, HostState.Unreachable, trigger: TransitionTrigger.ParentDown)));
        Assert.False(WebhookFilter.IsCriticalEvent(T(HostState.Unreachable, HostState.Down)));
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Down, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), true, true, critical: true));
    }

    [Fact]
    public void A_released_critical_target_still_down_is_the_same_incident_not_a_new_failure()
    {
        var stillDown = T(HostState.Starting, HostState.Down) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, AncestorId = Guid.NewGuid() },
        };
        Assert.False(WebhookFilter.IsCriticalEvent(stillDown));
        Assert.False(WebhookFilter.ShouldDeliver(stillDown, true, true, critical: true));
        Assert.False(WebhookFilter.IsCriticalEvent(stillDown with { To = HostState.Unreachable }));
    }

    [Fact]
    public void A_cold_start_critical_target_going_down_is_a_failure()
    {
        var coldDown = T(HostState.Starting, HostState.Down);
        Assert.True(WebhookFilter.IsCriticalEvent(coldDown));
        Assert.True(WebhookFilter.ShouldDeliver(coldDown, true, true, critical: true));
    }

    [Fact]
    public void A_released_critical_target_that_turns_out_faltering_is_a_recovery()
    {
        var faltering = T(HostState.Starting, HostState.Faltering) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, AncestorId = Guid.NewGuid() },
        };
        Assert.True(WebhookFilter.IsCriticalEvent(faltering));
    }

    [Fact]
    public void Other_transitions_of_a_critical_target_follow_the_normal_filters()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Faltering), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Faltering), false, true, critical: true));
    }

    [Fact]
    public void A_non_critical_target_is_unchanged_by_the_new_parameter()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), true, true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), false, true));
    }

    [Fact]
    public void A_DnsDown_verdict_passes_verdict_only_and_a_resolver_target_flap_is_a_target_like_any_other()
    {
        var verdict = T(HostState.Responding, HostState.Down, TransitionSubjectKind.Verdict, TransitionTrigger.VerdictChanged) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.VerdictChanged, Verdict = "DnsDown" },
        };
        Assert.True(WebhookFilter.ShouldDeliver(verdict, verdictOnly: true, skipExplained: true));
        // The scope filter lives in WebhookNotifier.Attach (IsSideSubject); the pure filter only sees kinds.
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), verdictOnly: true, skipExplained: true));
    }
}
