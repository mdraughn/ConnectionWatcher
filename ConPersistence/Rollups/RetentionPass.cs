using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Rollups;

/// <summary>
/// Downsamples aged rollups: minute rows older than the minute-retention
/// window become hourly rows; hourly rows older than the hourly-retention
/// window become daily rows. Each (target, coarse period) batch is aggregated,
/// upserted, and its source rows deleted in one transaction — so a crash
/// mid-run never double-counts and re-running is a no-op.
/// </summary>
public class RetentionPass(ILogger log)
{
    /// <summary>Runs both downsampling stages. Returns the number of source rows converted.</summary>
    public async Task<int> RunAsync(ConWatchDbContext db, DateTime nowUtc,
        PersistenceOptions options, CancellationToken ct)
    {
        int converted = 0;
        converted += await DownsampleAsync(db, "Minute", "Hour",
            nowUtc.AddDays(-options.MinuteRetentionDays), TruncateToHour, ct);
        converted += await DownsampleAsync(db, "Hour", "Day",
            nowUtc.AddMonths(-options.HourlyRetentionMonths), TruncateToDay, ct);
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
