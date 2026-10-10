using ConCore.Targets;
using ConEngine;
using ConPersistence.Warnings;

namespace ConWatch;

/// <summary>
/// What GET /api/v1/status answers, the SignalR statusChanged push carries,
/// the dashboard embeds for its first paint and the MCP status tool reports:
/// one shape. Positional records serialize in parameter order, which is the
/// order the anonymous objects they replace used.
/// </summary>
public sealed record StatusSnapshot(bool Enabled, StatusVerdict? Verdict,
    IReadOnlyList<StatusTarget> Targets, IReadOnlyList<StatusPool> Pools,
    IReadOnlyList<StatusWarning> Warnings)
{
    /// <summary>Archive mode (monitoring disabled): no live states.</summary>
    public static StatusSnapshot Disabled { get; } = new(false, null, [], [], []);
}

/// <param name="Operator">The ISP blamed by a Link* verdict ("Comcast"), else null.</param>
public sealed record StatusVerdict(string State, string? Detail, string? Operator);

/// <param name="Operator">The target's operator/ISP tag, or null.</param>
/// <param name="Critical">Notify when this is down, even if the internet is fine (spec §4.7).</param>
/// <param name="ExplainedBy">The root cause (target id) explaining this target's
/// Unreachable state, or null.</param>
public sealed record StatusTarget(Guid Id, string Name, string Address, string? Probe, string State,
    bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator,
    TargetScope Scope, bool Critical, Guid? ExplainedBy);

public sealed record StatusPool(Guid Id, string Name, string State);

/// <summary>An admin note (model warning, spec 2026-10-09 §4.6): where the map
/// disagrees with what the probes prove. Read-only here; never part of the verdict.</summary>
/// <param name="Kind">"HopBypassed", "NeverAnswered" or "UnprovableUp".</param>
public sealed record StatusWarning(string Key, string Kind, Guid? TargetId, string? TargetName,
    string Message, DateTimeOffset Since, bool Acknowledged);

public static class StatusSnapshotBuilder
{
    public static StatusSnapshot? Build(WatcherEngine? engine, ModelWarningLedger warnings)
    {
        if (engine is null)
            return null;

        var verdict = engine.Verdict?.Current is { } c
            ? new StatusVerdict(c.State.ToString(), c.Detail, c.Operator)
            : null;
        var targets = engine.Monitors.Values.Select(m => new StatusTarget(
                m.Target.Id,
                m.Target.Name,
                m.ResolvedAddress?.ToString() ?? m.Target.Address,
                m.Target.Probe,
                m.CurrentState.ToString(),
                m.IsAssumed,
                (double)(m.LastStatus?.Host.SuccessRate ?? 0f),
                (double)(m.LastStatus?.Host.AverageRoundtripTime ?? 0f),
                m.LastStatus?.Host.SampleSize ?? 0,
                m.PingCount,
                m.Target.Operator,
                m.Target.Scope,
                m.Target.Critical,
                m.ExplainedBy))
            .OrderBy(t => t.Name).ToList();
        var pools = engine.PoolCoordinators
            .Select(p => new StatusPool(p.Pool.Id, p.Pool.Name, p.CurrentState.ToString()))
            .OrderBy(p => p.Name).ToList();

        var notes = warnings.Current.Select(w => new StatusWarning(w.Key, w.Kind.ToString(),
                w.TargetId, w.TargetName, w.Message, w.SinceUtc, w.Acknowledged))
            .ToList();

        return new StatusSnapshot(true, verdict, targets, pools, notes);
    }
}
