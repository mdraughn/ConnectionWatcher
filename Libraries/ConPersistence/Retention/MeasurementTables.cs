using ConPersistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Retention;

/// <summary>
/// The measurement tables (history, as opposed to configuration) and the
/// deletes that act on them, in one place so prune, the nightly retention
/// pass, clear-history and factory reset cannot drift apart.
/// </summary>
public static class MeasurementTables
{
    /// <summary>Deletes every row of every measurement table: transitions,
    /// rollups, model warnings and glitches. The caller owns the transaction.</summary>
    public static async Task ClearAllAsync(ConWatchDbContext db, CancellationToken ct = default)
    {
        await db.Transitions.ExecuteDeleteAsync(ct);
        await db.Rollups.ExecuteDeleteAsync(ct);
        await db.ModelWarnings.ExecuteDeleteAsync(ct);
        await db.Glitches.ExecuteDeleteAsync(ct);
    }

    /// <summary>Deletes transitions older than the cutoff. Each subject's most
    /// recent transition always survives: warm restart seeds the presumed
    /// state from it, however old it is.</summary>
    public static Task<int> DeleteTransitionsBeforeAsync(ConWatchDbContext db,
        DateTime beforeUtc, CancellationToken ct = default)
    {
        var latestIds = HistoryQueries.LatestTransitionIdsPerSubject(db);
        return db.Transitions
            .Where(t => t.TimestampUtc < beforeUtc && !latestIds.Contains(t.Id))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Deletes model warnings that ended before the cutoff. An open
    /// one stays however old it is.</summary>
    public static Task<int> DeleteEndedModelWarningsBeforeAsync(ConWatchDbContext db,
        DateTime beforeUtc, CancellationToken ct = default) =>
        db.ModelWarnings
            .Where(w => w.EndUtc != null && w.EndUtc < beforeUtc)
            .ExecuteDeleteAsync(ct);
}
