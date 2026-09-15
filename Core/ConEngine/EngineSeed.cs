using ConEngine.Topology;
using ConEngine.Tracking;

namespace ConEngine;

/// <summary>A presumed state for one target: applied only when the address
/// and probe still match current configuration (a repointed or re-probed target cold-starts).</summary>
public sealed record TargetSeed(HostState State, string Address, string? Probe = null);

/// <summary>
/// Presumed subject states for a warm start: shown immediately (marked
/// assumed), confirmed or corrected by fresh pings. Built from the last
/// recorded transitions (process restart) or the outgoing engine's live
/// states (config rebuild). See docs/superpowers/specs/2026-09-11-warm-restart-design.md.
/// </summary>
public sealed class EngineSeed
{
    public Dictionary<Guid, TargetSeed> Targets { get; init; } = new();
    public Dictionary<Guid, HostState> Pools { get; init; } = new();
    public VerdictState? Verdict { get; init; }

    public bool IsEmpty => Targets.Count == 0 && Pools.Count == 0 && Verdict is null;
}
