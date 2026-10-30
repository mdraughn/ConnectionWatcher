using ConPersistence.Entities;
using ConPersistence.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Retention;

/// <summary>
/// Downsamples aged rollups: minute rows older than the minute-retention
/// window become hourly rows; hourly rows older than the hourly-retention
/// window become daily rows. Each (target, coarse period) batch is aggregated,
/// upserted, and its source rows deleted in one transaction — so a crash
/// mid-run never double-counts and re-running is a no-op.
/// </summary>
public class RetentionPass(ILogger log)
{
    /// <summary>Runs both downsampling stages and the two prunes. Returns the
    /// rows it changed (fine rollups folded into coarser ones, plus glitches
    /// and transitions pruned): 0 on an immediate second run.</summary>
    public async Task<int> RunAsync(ConWatchDbContext db, DateTime nowUtc,
        RetentionWindows windows, CancellationToken ct)
    {
        int converted = 0;
        converted += await DownsampleAsync(db, RollupResolution.Minute, RollupResolution.Hour,
            nowUtc.AddDays(-windows.MinuteDays), TruncateToHour, ct);
        converted += await DownsampleAsync(db, RollupResolution.Hour, RollupResolution.Day,
            nowUtc.AddMonths(-windows.HourlyMonths), TruncateToDay, ct);

        // Glitches are fine-grained events; they age out with the minute data.
        var glitchThreshold = nowUtc.AddDays(-windows.MinuteDays);
        var pruned = await db.Glitches.Where(g => g.StartUtc < glitchThreshold)
            .ExecuteDeleteAsync(ct);
        if (pruned > 0)
        {
            log.LogInformation("Pruned {Count} glitches older than {Days} days",
                pruned, windows.MinuteDays);
            converted += pruned;
        }

        // Transitions age out too — the one otherwise-unbounded table. Each
        // subject's MOST RECENT transition always survives: warm restart
        // seeds the presumed state from it, however old it is.
        var transitionThreshold = nowUtc.AddMonths(-windows.TransitionMonths);
        var prunedTransitions = await MeasurementTables.DeleteTransitionsBeforeAsync(
            db, transitionThreshold, ct);
        if (prunedTransitions > 0)
        {
            log.LogInformation("Pruned {Count} transitions older than {Months} months",
                prunedTransitions, windows.TransitionMonths);
            converted += prunedTransitions;
        }

        // Ended model warnings (admin notes) age out with transitions; an
        // open one stays however old it is.
        var prunedWarnings = await MeasurementTables.DeleteEndedModelWarningsBeforeAsync(
            db, transitionThreshold, ct);
        if (prunedWarnings > 0)
        {
            log.LogInformation("Pruned {Count} model warnings older than {Months} months",
                prunedWarnings, windows.TransitionMonths);
            converted += prunedWarnings;
        }
        return converted;
    }

    private async Task<int> DownsampleAsync(ConWatchDbContext db, string fineResolution,
        string coarseResolution, DateTime threshold, Func<DateTime, DateTime> truncate,
        CancellationToken ct)
    {
        var fineRows = await db.Rollups
            .Where(r => r.Resolution == fineResolution && r.PeriodStartUtc < threshold)
            .OrderBy(r => r.TargetId).ThenBy(r => r.PeriodStartUtc)
            .ToListAsync(ct);
        if (fineRows.Count == 0)
            return 0;

        foreach (var group in fineRows.GroupBy(r => (r.TargetId, Period: truncate(r.PeriodStartUtc))))
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var target = await db.Rollups.SingleOrDefaultAsync(r =>
                r.TargetId == group.Key.TargetId && r.Resolution == coarseResolution &&
                r.PeriodStartUtc == group.Key.Period, ct);
            if (target is null)
            {
                target = new RollupRow
                {
                    TargetId = group.Key.TargetId,
                    PeriodStartUtc = group.Key.Period,
                    Resolution = coarseResolution,
                };
                db.Rollups.Add(target);
            }
            foreach (var row in group)
                RollupMerge.Combine(target, row);
            db.Rollups.RemoveRange(group);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        log.LogInformation("Downsampled {Count} {Fine} rows into {Coarse} rows",
            fineRows.Count, fineResolution, coarseResolution);
        return fineRows.Count;
    }

    private static DateTime TruncateToHour(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);

    private static DateTime TruncateToDay(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerDay, DateTimeKind.Utc);
}
