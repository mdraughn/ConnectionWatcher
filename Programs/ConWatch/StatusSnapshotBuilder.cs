using ConEngine;

namespace ConWatch;

/// <summary>
/// Builds the status snapshot shared by GET /api/v1/status and the SignalR
/// statusChanged push, so both surfaces always agree on shape.
/// </summary>
public static class StatusSnapshotBuilder
{
    public static object? Build(WatcherEngine? engine)
    {
        if (engine is null)
            return null;

        var verdict = engine.Verdict is { } v
            ? new
            {
                State = v.CurrentVerdict.ToString(),
                Detail = v.CurrentDetail,
                Operator = v.CurrentOperator,
            }
            : null;
        var targets = engine.Monitors.Values.Select(m => new
        {
            Id = m.Target.Id,
            Name = m.Target.Name,
            Address = m.ResolvedAddress?.ToString() ?? m.Target.Address,
            State = m.CurrentState.ToString(),
            Assumed = m.IsAssumed,
            SuccessRate = (double)(m.LastStatus?.Host.SuccessRate ?? 0f),
            AvgRtt = (double)(m.LastStatus?.Host.AverageRoundtripTime ?? 0f),
            SampleSize = m.LastStatus?.Host.SampleSize ?? 0,
        }).OrderBy(t => t.Name).ToList();
        var pools = engine.PoolCoordinators.Select(p => new
        {
            Id = p.Pool.Id,
            Name = p.Pool.Name,
            State = p.CurrentState.ToString(),
        }).OrderBy(p => p.Name).ToList();

        return new { Verdict = verdict, Targets = targets, Pools = pools };
    }
}
