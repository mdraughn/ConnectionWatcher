namespace ConWatch.Api;

/// <summary>Live status from the running engine.</summary>
public static class StatusEndpoints
{
    public static void MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet("/status", (WatcherHostService host) =>
        {
            var engine = host.Engine;
            if (engine is null)
                return Results.Json(new { error = "Engine is not running" }, statusCode: 503);

            var verdict = engine.Verdict is { } v
                ? new { State = v.CurrentVerdict.ToString(), Detail = v.CurrentDetail }
                : null;
            var targets = engine.Monitors.Values.Select(m => new
            {
                Id = m.Target.Id,
                Name = m.Target.Name,
                Address = m.ResolvedAddress?.ToString() ?? m.Target.Address,
                State = m.CurrentState.ToString(),
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

            return Results.Ok(new { Verdict = verdict, Targets = targets, Pools = pools });
        });
    }
}
