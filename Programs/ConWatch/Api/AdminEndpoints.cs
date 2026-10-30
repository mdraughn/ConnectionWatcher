using ConEngine.Configuration;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConWatch.Api;

/// <summary>Database stats and the danger zone (clear history / factory reset).</summary>
public static class AdminEndpoints
{
    public record ConfirmRequest(string? Confirm);

    public record PruneRequest(DateTime? BeforeUtc, string? Confirm);

    public record MqttTestRequest(string? Host, int? Port, string? Username, string? Password);

    public record EngineControlRequest(bool Enabled, string? Password);

    /// <summary>
    /// Machine-to-machine master enable control for the take-over
    /// choreography: mapped on the OPEN group with the admin password
    /// verified inline (like /auth/login — no cross-node cookie dance).
    /// Applies the setting and rebuilds immediately, so a snapshot taken
    /// right after a disable already contains every flushed row.
    /// </summary>
    public static void MapEngineControl(this RouteGroupBuilder openApi)
    {
        openApi.MapPost("/admin/engine", async (EngineControlRequest request,
            ConWatch.Auth.AdminGate gate, SettingsStore settings, WatcherHostService host) =>
        {
            if (!await gate.VerifyOrDelayAsync(request.Password))
                return Results.Unauthorized();
            await settings.SetManyAsync(new Dictionary<string, string?>
            {
                [SettingKeys.EngineEnabled] = request.Enabled ? "true" : "false",
            });
            if (!await host.RebuildIfStaleAsync()) // effective before we answer
                return ApiResults.Error(StatusCodes.Status500InternalServerError, null,
                    "The engine could not be rebuilt; see the log.");
            return Results.NoContent();
        });
    }

    public static void MapAdmin(this RouteGroupBuilder api)
    {
        api.MapGet("/admin/stats", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            IOptions<PersistenceOptions> options, SettingsStore settings) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var path = Path.GetFullPath(options.Value.DbPath);
            long fileSize = File.Exists(path) ? new FileInfo(path).Length : 0;
            var oldestTransition = await db.Transitions.OrderBy(t => t.TimestampUtc)
                .Select(t => (DateTime?)t.TimestampUtc).FirstOrDefaultAsync();
            var oldestRollup = await db.Rollups.OrderBy(r => r.PeriodStartUtc)
                .Select(r => (DateTime?)r.PeriodStartUtc).FirstOrDefaultAsync();
            return Results.Ok(new
            {
                FileSizeBytes = fileSize,
                Tables = new
                {
                    Profiles = await db.Profiles.CountAsync(),
                    Targets = await db.Targets.CountAsync(),
                    Pools = await db.Pools.CountAsync(),
                    PoolMembers = await db.PoolMembers.CountAsync(),
                    Transitions = await db.Transitions.CountAsync(),
                    Rollups = await db.Rollups.CountAsync(),
                    ModelWarnings = await db.ModelWarnings.CountAsync(),
                    Glitches = await db.Glitches.CountAsync(),
                    Discoveries = await db.Discoveries.CountAsync(),
                    Settings = await db.Settings.CountAsync(),
                },
                OldestTransitionUtc = AsUtc(oldestTransition),
                OldestRollupUtc = AsUtc(oldestRollup),
                LastRetentionRunUtc = await settings.GetAsync(SettingKeys.RetentionLastRunUtc),
            });

            static DateTimeOffset? AsUtc(DateTime? dt) => dt is { } d ? new DateTimeOffset(d) : null;
        });

        // Tests the broker with the values in the form, saved or not.
        api.MapPost("/admin/mqtt-test", async (MqttTestRequest request, CancellationToken ct) =>
        {
            if (request.Host is not { Length: > 0 })
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "Host is required.");
            var portRule = SettingRules.Int(SettingKeys.MqttPort);
            var port = request.Port ?? portRule.Default;
            if (port < portRule.Min || port > portRule.Max)
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, $"Port must be {portRule.Min}–{portRule.Max}.");

            var (ok, message) = await ConNotify.MqttTester.TestAsync(
                request.Host, port, request.Username, request.Password, ct);
            return Results.Ok(new { ok, message });
        });

        api.MapPost("/admin/prune", async (PruneRequest request,
            IDbContextFactory<ConWatchDbContext> dbFactory, IOptions<PersistenceOptions> options,
            ILoggerFactory loggerFactory) =>
        {
            if (request.Confirm != "PRUNE")
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "Confirmation must be exactly 'PRUNE'.");
            if (request.BeforeUtc is not { } before || before == default(DateTime))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "A cutoff date is required.");

            await using var db = await dbFactory.CreateDbContextAsync();
            var result = await ConPersistence.Retention.DataPruner.PruneBeforeAsync(
                db, options.Value.DbPath, TimeRange.AsUtc(before));
            loggerFactory.CreateLogger("Admin").LogWarning(
                "History PRUNED before {Before:u}: {Transitions} transitions, {Rollups} rollups, " +
                "{Glitches} glitches, {ModelWarnings} model warnings; file {Before2} -> {After} bytes",
                before, result.Transitions, result.Rollups, result.Glitches, result.ModelWarnings,
                result.FileSizeBefore, result.FileSizeAfter);
            return Results.Ok(result);
        });

        api.MapPost("/admin/clear-history", async (ConfirmRequest request,
            IDbContextFactory<ConWatchDbContext> dbFactory, ILoggerFactory loggerFactory) =>
        {
            if (request.Confirm != "CLEAR")
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "Confirmation must be exactly 'CLEAR'.");

            await using var db = await dbFactory.CreateDbContextAsync();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await ConPersistence.Retention.MeasurementTables.ClearAllAsync(db);
                await tx.CommitAsync();
            }
            loggerFactory.CreateLogger("Admin").LogWarning("Measurement history CLEARED by administrator");
            return Results.NoContent();
        });

        api.MapPost("/admin/factory-reset", async (ConfirmRequest request,
            IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore store,
            SettingsStore settings, WatcherHostService host, ILoggerFactory loggerFactory,
            IOptions<VerdictOptions> verdictOptions, IOptions<PersistenceOptions> persistenceOptions) =>
        {
            if (request.Confirm != "RESET")
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "Confirmation must be exactly 'RESET'.");

            await using (var db = await dbFactory.CreateDbContextAsync())
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await ConPersistence.Retention.MeasurementTables.ClearAllAsync(db);
                await db.PoolMembers.ExecuteDeleteAsync();
                await db.Pools.ExecuteDeleteAsync();
                await db.Targets.ExecuteDeleteAsync();
                await db.Profiles.ExecuteDeleteAsync();
                await db.Settings.ExecuteDeleteAsync();
                await db.Discoveries.ExecuteDeleteAsync();
                await tx.CommitAsync();
            }
            await store.SeedAsync(null); // builtin profiles
            await settings.SeedMissingAsync(
                SettingsDefaults.For(verdictOptions.Value, persistenceOptions.Value)); // as first boot
            loggerFactory.CreateLogger("Admin").LogWarning("FACTORY RESET performed by administrator");
            if (!await host.RebuildAsync())
                return ApiResults.Error(StatusCodes.Status500InternalServerError, null,
                    "The engine could not be rebuilt; see the log.");
            return Results.NoContent();
        });
    }
}
