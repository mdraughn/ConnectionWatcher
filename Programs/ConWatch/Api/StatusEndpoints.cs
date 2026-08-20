namespace ConWatch.Api;

/// <summary>Live status from the running engine.</summary>
public static class StatusEndpoints
{
    public static void MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet("/status", (WatcherHostService host) =>
            StatusSnapshotBuilder.Build(host.Engine) is { } snapshot
                ? Results.Ok(snapshot)
                : Results.Json(new { error = "Engine is not running" }, statusCode: 503));
    }
}
