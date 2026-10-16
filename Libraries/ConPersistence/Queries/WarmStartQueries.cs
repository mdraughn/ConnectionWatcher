using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Queries;

/// <summary>Read-side queries for warm restart: what the world looked like
/// when this instance was last alive.</summary>
public static class WarmStartQueries
{
    /// <summary>
    /// When the watcher last observably ran: the latest transition timestamp
    /// or the end of the latest minute rollup, whichever is later. Null with
    /// no data at all (fresh install).
    /// </summary>
    public static async Task<DateTime?> LastAliveUtcAsync(ConWatchDbContext db)
    {
        var lastTransition = await db.Transitions.AsNoTracking()
            .OrderByDescending(t => t.Id)
            .Select(t => (DateTime?)t.TimestampUtc)
            .FirstOrDefaultAsync();
        var lastRollupStart = await db.Rollups.AsNoTracking()
            .Where(r => r.Resolution == RollupResolution.Minute)
            .OrderByDescending(r => r.PeriodStartUtc)
            .Select(r => (DateTime?)r.PeriodStartUtc)
            .FirstOrDefaultAsync();
        var lastRollup = lastRollupStart?.AddMinutes(1);
        if (lastTransition is null)
            return lastRollup;
        if (lastRollup is null)
            return lastTransition;
        return lastTransition > lastRollup ? lastTransition : lastRollup;
    }

    /// <summary>The latest transition row per subject (any kind).</summary>
    public static Task<List<TransitionRow>> LatestTransitionsPerSubjectAsync(ConWatchDbContext db)
    {
        var latestIds = HistoryQueries.LatestTransitionIdsPerSubject(db);
        return db.Transitions.AsNoTracking().Where(t => latestIds.Contains(t.Id)).ToListAsync();
    }
}
