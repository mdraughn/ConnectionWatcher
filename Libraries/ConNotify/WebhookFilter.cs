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
    /// <param name="critical">The subject is a target marked critical: its own
    /// failure and recovery are always delivered, past "verdict only" and
    /// "skip explained" (spec 2026-10-09 §4.7: critical means tell me).</param>
    public static bool ShouldDeliver(TargetTransition t, bool verdictOnly, bool skipExplained,
        bool critical = false)
    {
        if (critical && IsCriticalEvent(t))
            return true;
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

    /// <summary>A target's own failure (entering Down/Unreachable from neither)
    /// or recovery (leaving them for an answering state, or a released
    /// target's first answer naming its former cause). Down↔Unreachable is a
    /// reclassification, not a new event. A released target still down (Starting
    /// into Down/Unreachable, naming its former cause) is the same incident, whose
    /// failure was already delivered.</summary>
    public static bool IsCriticalEvent(TargetTransition t)
    {
        if (t.Kind != TransitionSubjectKind.Target)
            return false;
        static bool Failed(HostState s) => s is HostState.Down or HostState.Unreachable;
        static bool Answering(HostState s) =>
            s is HostState.Responding or HostState.Recovering or HostState.Faltering;
        if (Failed(t.To))
            return !Failed(t.From) &&
                !(t.From == HostState.Starting && t.Reason.AncestorId is not null);
        if (Failed(t.From))
            return Answering(t.To);
        return t.From == HostState.Starting && t.Reason.AncestorId is not null && Answering(t.To);
    }
}
