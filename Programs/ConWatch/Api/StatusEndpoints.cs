namespace ConWatch.Api;

/// <summary>Live status and health from the running engine.</summary>
public static class StatusEndpoints
{
    public static void MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet("/status", (WatcherHostService host, ConPersistence.Warnings.ModelWarningLedger warnings) =>
        {
            if (StatusSnapshotBuilder.Build(host.Engine, warnings) is { } snapshot)
                return Results.Ok(snapshot);
            // Archive mode is a healthy answer, not an error: no live states.
            if (!host.EngineEnabled)
                return Results.Ok(StatusSnapshot.Disabled);
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, null, "Engine is not running");
        });

        api.MapGet("/health", (WatcherHostService host, TimeProvider time) =>
        {
            var engine = host.Engine;
            var now = time.GetUtcNow();
            var verdict = EngineHealth.Classify(engine, host.EngineEnabled, now, host.LastStartFailure);
            var body = new
            {
                // A deliberately disabled node is a healthy archive (200) — container
                // healthchecks must not flag it; 503 stays for enabled-but-down,
                // stalled, and config the validator rejected (ConfigInvalid).
                Status = verdict.Status,
                Enabled = host.EngineEnabled,
                EngineRunning = engine is not null,
                // Null unless the last start attempt failed validation: the
                // operator's actual repair instruction.
                StartFailure = host.LastStartFailure,
                Stalled = verdict.Stalled,
                SilenceSeconds = verdict.SilenceFor?.TotalSeconds,
                TargetCount = engine?.Monitors.Count ?? 0,
                TimestampUtc = now,
                Version = BuildInfo.Version,
                BuiltUtc = BuildInfo.BuiltUtc,
            };
            var ok = verdict.Status is "Healthy" or "Disabled";
            return ok ? Results.Ok(body) : Results.Json(body, statusCode: 503);
        });
    }
}
