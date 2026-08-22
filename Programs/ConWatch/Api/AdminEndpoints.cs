using ConPersistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConWatch.Api;

/// <summary>Database stats and the danger zone (clear history / factory reset).</summary>
public static class AdminEndpoints
{
    public record ConfirmRequest(string? Confirm);

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
                    Settings = await db.Settings.CountAsync(),
                },
                OldestTransitionUtc = oldestTransition,
                OldestRollupUtc = oldestRollup,
                LastRetentionRunUtc = await settings.GetAsync(SettingKeys.RetentionLastRunUtc),
            });
        });

        api.MapPost("/admin/clear-history", async (ConfirmRequest request,
            IDbContextFactory<ConWatchDbContext> dbFactory, ILoggerFactory loggerFactory) =>
        {
            if (request.Confirm != "CLEAR")
                return Results.BadRequest(new { errors = new[] { "Confirmation must be exactly 'CLEAR'." } });

            await using var db = await dbFactory.CreateDbContextAsync();
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await db.Transitions.ExecuteDeleteAsync();
                await db.Rollups.ExecuteDeleteAsync();
                await tx.CommitAsync();
            }
            await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;");
            loggerFactory.CreateLogger("Admin").LogWarning("Measurement history CLEARED by administrator");
            return Results.NoContent();
        });

        api.MapPost("/admin/factory-reset", async (ConfirmRequest request,
            IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore store,
            SettingsStore settings, WatcherHostService host, ILoggerFactory loggerFactory) =>
        {
            if (request.Confirm != "RESET")
                return Results.BadRequest(new { errors = new[] { "Confirmation must be exactly 'RESET'." } });

            await using (var db = await dbFactory.CreateDbContextAsync())
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await db.Transitions.ExecuteDeleteAsync();
                await db.Rollups.ExecuteDeleteAsync();
                await db.PoolMembers.ExecuteDeleteAsync();
                await db.Pools.ExecuteDeleteAsync();
                await db.Targets.ExecuteDeleteAsync();
                await db.Profiles.ExecuteDeleteAsync();
                await db.Settings.ExecuteDeleteAsync();
                await tx.CommitAsync();
            }
            await store.SeedAsync(null); // builtin profiles
            await settings.SeedMissingAsync(new Dictionary<string, string>
            {
                [SettingKeys.WeatherThreshold] = "2",
                [SettingKeys.MinuteRetentionDays] = "90",
                [SettingKeys.HourlyRetentionMonths] = "13",
                [SettingKeys.RetentionRunHourUtc] = "3",
            });
            loggerFactory.CreateLogger("Admin").LogWarning("FACTORY RESET performed by administrator");
            await host.RebuildAsync();
            return Results.NoContent();
        });
    }
}
