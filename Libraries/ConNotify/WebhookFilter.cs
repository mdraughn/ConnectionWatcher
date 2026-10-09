using ConEngine.Events;
using ConEngine.Tracking;

namespace ConNotify;

/// <summary>Which transitions a webhook receives (pure, unit-tested).</summary>
public static class WebhookFilter
{
    /// <param name="verdictOnly">Only the headline verdict.</param>
    /// <param name="skipExplained">Drop a failure explained by an upstream root
    /// cause, and that failure's recovery: one router outage is one incident,
    /// not one per target behind it (spec 2026-10-09 §4.4, D7). A target
    /// released when its cause went away re-proves from Starting; its first
    /// transition out of Starting names the former cause (AncestorId), and if
    /// that is a recovery it belongs to the incident too. Still Down after
    /// its cause recovered is its own problem and is delivered.</param>
    public static bool ShouldDeliver(TargetTransition t, bool verdictOnly, bool skipExplained)
    {
        if (verdictOnly && t.Kind != TransitionSubjectKind.Verdict)
            return false;
        if (skipExplained && t.Kind == TransitionSubjectKind.Target)
        {
            if (t.To == HostState.Unreachable)
                return false;
            if (t.From == HostState.Unreachable &&
                t.To is HostState.Starting or HostState.Recovering or HostState.Responding)
                return false;
            if (t.From == HostState.Starting && t.Reason.AncestorId is not null &&
                t.To is HostState.Responding or HostState.Recovering)
                return false;
        }
        return true;
    }
}
