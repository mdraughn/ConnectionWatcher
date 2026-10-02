using ConCore.Targets;
using ConEngine;

namespace ConWatch;

/// <summary>
/// What GET /api/v1/status answers, the SignalR statusChanged push carries,
/// the dashboard embeds for its first paint and the MCP status tool reports:
/// one shape. Positional records serialize in parameter order, which is the
/// order the anonymous objects they replace used.
/// </summary>
public sealed record StatusSnapshot(bool Enabled, StatusVerdict? Verdict,
    IReadOnlyList<StatusTarget> Targets, IReadOnlyList<StatusPool> Pools)
{
    /// <summary>Archive mode (monitoring disabled): no live states.</summary>
    public static StatusSnapshot Disabled { get; } = new(false, null, [], []);
}

/// <param name="Operator">The ISP blamed by a Link* verdict ("Comcast"), else null.</param>
public sealed record StatusVerdict(string State, string? Detail, string? Operator);

/// <param name="Operator">The target's operator/ISP tag, or null.</param>
public sealed record StatusTarget(Guid Id, string Name, string Address, string? Probe, string State,
    bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator,
    TargetScope Scope);

public sealed record StatusPool(Guid Id, string Name, string State);

public static class StatusSnapshotBuilder
{
    public static StatusSnapshot? Build(WatcherEngine? engine)
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
                m.Target.Scope))
            .OrderBy(t => t.Name).ToList();
        var pools = engine.PoolCoordinators
            .Select(p => new StatusPool(p.Pool.Id, p.Pool.Name, p.CurrentState.ToString()))
            .OrderBy(p => p.Name).ToList();

        return new StatusSnapshot(true, verdict, targets, pools);
    }
}
