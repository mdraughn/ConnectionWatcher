using ConPersistence.Rollups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConPersistence.Services;

/// <summary>
/// Runs the retention pass once at startup, then daily at the configured UTC hour.
/// </summary>
public class RetentionService(IDbContextFactory<ConWatchDbContext> dbFactory,
    IOptions<PersistenceOptions> options, TimeProvider timeProvider,
    ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pass = new RetentionPass(log);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
                var converted = await pass.RunAsync(db, timeProvider.GetUtcNow().UtcDateTime,
                    options.Value, stoppingToken).ConfigureAwait(false);
                if (converted > 0)
                    await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", stoppingToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Retention pass failed; will retry at next scheduled run");
            }

            try
            {
                await Task.Delay(DelayUntilNextRun(), timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private TimeSpan DelayUntilNextRun()
    {
        var now = timeProvider.GetUtcNow();
        var next = new DateTimeOffset(now.Year, now.Month, now.Day,
            options.Value.RetentionRunHourUtc, 0, 0, TimeSpan.Zero);
        if (next <= now)
            next = next.AddDays(1);
        return next - now;
    }
}
