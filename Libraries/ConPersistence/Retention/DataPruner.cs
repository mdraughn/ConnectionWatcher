using ConPersistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Retention;

/// <summary>Result of a user-initiated prune: what was deleted and the file
/// size before/after the VACUUM.</summary>
public sealed record PruneResult(int Transitions, int Rollups, int Glitches,
    long FileSizeBefore, long FileSizeAfter);

/// <summary>
/// User-initiated deletion of measurement data older than a date — beyond the
/// automatic downsampling. A prune is NOT a HistoryReset: it removes rows and
/// nothing else changes semantically. Each subject's most recent transition
/// always survives (warm restart seeds from it), matching the nightly
/// retention pass. Configuration and settings are never touched.
/// </summary>
public static class DataPruner
{
    public static async Task<PruneResult> PruneBeforeAsync(ConWatchDbContext db,
        string dbPath, DateTime beforeUtc, CancellationToken ct = default)
    {
        var sizeBefore = FileSize(dbPath);

        var latestIds = HistoryQueries.LatestTransitionIdsPerSubject(db);
        var transitions = await db.Transitions
            .Where(t => t.TimestampUtc < beforeUtc && !latestIds.Contains(t.Id))
            .ExecuteDeleteAsync(ct);
        var rollups = await db.Rollups
            .Where(r => r.PeriodStartUtc < beforeUtc)
            .ExecuteDeleteAsync(ct);
        var glitches = await db.Glitches
            .Where(g => g.StartUtc < beforeUtc)
            .ExecuteDeleteAsync(ct);

        // A big delete leaves the file at its high-water mark; a full VACUUM
        // rebuilds it and returns the space. Must run outside a transaction.
        await db.Database.ExecuteSqlRawAsync("VACUUM;", ct);

        return new PruneResult(transitions, rollups, glitches, sizeBefore, FileSize(dbPath));
    }

    private static long FileSize(string dbPath)
    {
        var path = Path.GetFullPath(dbPath);
        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }
}
