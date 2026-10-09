using ConEngine.Events;
using ConEngine.Tracking;

namespace ConNotify;

/// <summary>Which transitions a webhook receives (pure, unit-tested).</summary>
public static class WebhookFilter
{
    /// <param name="verdictOnly">Only the headline verdict.</param>
    /// <param name="skipExplained">Drop a failure explained by an upstream root
    /// cause, and that failure's recovery: one router outage is one incident,
    /// not one per target behind it (spec 2026-10-09 §4.4, D7).</param>
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
        }
        return true;
    }
}
