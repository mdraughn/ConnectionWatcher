using ConEngine.Events;

namespace ConEngine.Tracking;

/// <summary>
/// What one analysis step produced: always a status, sometimes a transition.
/// Returned rather than pushed so the caller can publish after releasing its
/// locks; publishing under a lock is how the ping thread and the DNS thread
/// deadlocked against each other through the coordinators.
/// </summary>
public sealed record AnalysisOutcome(HostStatus Status, AnalysisTransition? Transition);
