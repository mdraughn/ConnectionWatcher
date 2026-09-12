namespace ConWatch.Api;

/// <summary>Live status from the running engine.</summary>
public static class StatusEndpoints
{
    public static void MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet("/status", async (WatcherHostService host) =>
        {
            if (StatusSnapshotBuilder.Build(host.Engine) is { } snapshot)
                return Results.Ok(snapshot);
            // Archive mode is a healthy answer, not an error: no live states.
            if (!await host.EngineEnabledAsync())
                return Results.Ok(new
                {
                    Enabled = false,
                    Verdict = (object?)null,
                    Targets = Array.Empty<object>(),
                    Pools = Array.Empty<object>(),
                });
            return Results.Json(new { error = "Engine is not running" }, statusCode: 503);
        });
    }
}
