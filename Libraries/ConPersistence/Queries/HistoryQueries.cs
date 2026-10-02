using ConCore.Targets;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Queries;

/// <summary>Read-side queries for the transition log and rollups.</summary>
public static class HistoryQueries
{
    /// <summary>
    /// Transitions newest-first with keyset paging: pass the last seen row id
    /// as <paramref name="before"/> to get the next page.
    /// </summary>
    public static async Task<List<TransitionRow>> TransitionsAsync(ConWatchDbContext db,
        Guid? subjectId, DateTime? fromUtc, DateTime? toUtc, int limit, long? before,
        string? subjectName = null, ScopeFilter scope = ScopeFilter.All)
    {
        limit = Math.Clamp(limit, 1, 1000);
        IQueryable<TransitionRow> query = db.Transitions.AsNoTracking();
        if (subjectId is { } sid)
            query = query.Where(t => t.SubjectId == sid);
        if (subjectName is { Length: > 0 })
            query = query.Where(t => t.SubjectName == subjectName);
        if (fromUtc is { } from)
            query = query.Where(t => t.TimestampUtc >= from);
        if (toUtc is { } to)
            query = query.Where(t => t.TimestampUtc <= to);
        if (before is { } cursor)
            query = query.Where(t => t.Id < cursor);
        if (scope != ScopeFilter.All)
        {
            var localIds = db.Targets.Where(t => t.Scope == TargetScope.Local).Select(t => t.Id);
            query = scope == ScopeFilter.Local
                ? query.Where(t => localIds.Contains(t.SubjectId))
                : query.Where(t => !localIds.Contains(t.SubjectId));
        }
        return await query.OrderByDescending(t => t.Id).Take(limit).ToListAsync();
    }

    /// <summary>
    /// Picks the finest resolution whose retention window still contains
    /// <paramref name="fromUtc"/>; explicit requests pass through.
    /// </summary>
    public static string ResolveResolution(string requested, DateTime fromUtc, DateTime nowUtc,
        PersistenceOptions options)
    {
        if (!requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return requested.ToLowerInvariant() switch
            {
                "minute" => "Minute",
                "hour" => "Hour",
                "day" => "Day",
                _ => throw new ArgumentException($"Unknown resolution '{requested}'."),
            };
        }
        if (fromUtc >= nowUtc.AddDays(-options.MinuteRetentionDays))
            return "Minute";
        if (fromUtc >= nowUtc.AddMonths(-options.HourlyRetentionMonths))
            return "Hour";
        return "Day";
    }

    /// <summary>Rollup points for one target in ascending period order.</summary>
    public static Task<List<RollupRow>> RollupsAsync(ConWatchDbContext db,
        Guid targetId, DateTime fromUtc, DateTime toUtc, string resolution) =>
        db.Rollups.AsNoTracking()
            .Where(r => r.TargetId == targetId && r.Resolution == resolution &&
                        r.PeriodStartUtc >= fromUtc && r.PeriodStartUtc <= toUtc)
            .OrderBy(r => r.PeriodStartUtc)
            .ToListAsync();

    /// <summary>The ids of the current Local-scope targets, for tagging history rows with a scope.</summary>
    public static async Task<HashSet<Guid>> LocalTargetIdsAsync(ConWatchDbContext db) =>
        (await db.Targets.AsNoTracking().Where(t => t.Scope == TargetScope.Local)
            .Select(t => t.Id).ToListAsync()).ToHashSet();

    /// <summary>Glitches newest-first (by start, then id), optionally for one target and window.</summary>
    public static Task<List<GlitchRow>> GlitchesAsync(ConWatchDbContext db,
        Guid? targetId, DateTime? fromUtc, DateTime? toUtc, int limit,
        ScopeFilter scope = ScopeFilter.All)
    {
        limit = Math.Clamp(limit, 1, 1000);
        IQueryable<GlitchRow> query = db.Glitches.AsNoTracking();
        if (targetId is { } tid)
            query = query.Where(g => g.TargetId == tid);
        if (fromUtc is { } from)
            query = query.Where(g => g.StartUtc >= from);
        if (toUtc is { } to)
            query = query.Where(g => g.StartUtc <= to);
        if (scope != ScopeFilter.All)
        {
            var localIds = db.Targets.Where(t => t.Scope == TargetScope.Local).Select(t => t.Id);
            query = scope == ScopeFilter.Local
                ? query.Where(g => localIds.Contains(g.TargetId))
                : query.Where(g => !localIds.Contains(g.TargetId));
        }
        return query.OrderByDescending(g => g.StartUtc).ThenByDescending(g => g.Id)
            .Take(limit).ToListAsync();
    }

    /// <summary>
    /// The id of each subject's most recent transition. Every retention and
    /// prune path must keep these rows: warm restart seeds the presumed state
    /// from them, however old they are.
    /// </summary>
    public static IQueryable<long> LatestTransitionIdsPerSubject(ConWatchDbContext db) =>
        db.Transitions.GroupBy(t => t.SubjectId).Select(g => g.Max(t => t.Id));
}
