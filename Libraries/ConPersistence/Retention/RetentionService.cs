using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConPersistence.Retention;

/// <summary>
/// Runs the retention pass once at startup, then daily at the configured UTC hour.
/// </summary>
public class RetentionService(IDbContextFactory<ConWatchDbContext> dbFactory,
    IOptions<PersistenceOptions> options, TimeProvider timeProvider,
    ILogger<RetentionService> log, SettingsStore? settingsStore = null) : BackgroundService
{
    /// <summary>Appsettings values overridden by DB settings when present.</summary>
    private async Task<PersistenceOptions> EffectiveOptionsAsync()
    {
        var effective = options.Value;
        if (settingsStore is null)
            return effective;
        return new PersistenceOptions
        {
            DbPath = effective.DbPath,
            RebuildDebounceSeconds = effective.RebuildDebounceSeconds,
            MinuteRetentionDays = await settingsStore.GetIntAsync(
                SettingKeys.MinuteRetentionDays, effective.MinuteRetentionDays),
            HourlyRetentionMonths = await settingsStore.GetIntAsync(
                SettingKeys.HourlyRetentionMonths, effective.HourlyRetentionMonths),
            TransitionRetentionMonths = await settingsStore.GetIntAsync(
                SettingKeys.TransitionRetentionMonths, effective.TransitionRetentionMonths),
            RetentionRunHourUtc = await settingsStore.GetIntAsync(
                SettingKeys.RetentionRunHourUtc, effective.RetentionRunHourUtc),
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pass = new RetentionPass(log);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
                await pass.RunAsync(db, timeProvider.GetUtcNow().UtcDateTime,
                    await EffectiveOptionsAsync().ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
                if (settingsStore is not null)
                    await settingsStore.SetManyAsync(new Dictionary<string, string?>
                    {
                        [SettingKeys.RetentionLastRunUtc] = timeProvider.GetUtcNow().ToString("O"),
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Retention pass failed; will retry at next scheduled run");
            }

            TimeSpan delay;
            try
            {
                delay = await DelayUntilNextRunAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Settings unreadable (e.g. transient DB issue): never let the
                // scheduler kill the host — retry in an hour.
                log.LogWarning(ex, "Could not compute next retention run; retrying in 1h");
                delay = TimeSpan.FromHours(1);
            }
            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<TimeSpan> DelayUntilNextRunAsync()
    {
        var now = timeProvider.GetUtcNow();
        var hour = (await EffectiveOptionsAsync().ConfigureAwait(false)).RetentionRunHourUtc;
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, TimeSpan.Zero);
        if (next <= now)
            next = next.AddDays(1);
        return next - now;
    }
}
