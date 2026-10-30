using ConCore.Targets;
using ConEngine.Events;
using ConPersistence.Entities;
using ConPersistence.Retention;
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
        IQueryable<TransitionRow> query = TransitionsQuery(db, subjectId, fromUtc, toUtc, subjectName, scope);
        if (before is { } cursor)
            query = query.Where(t => t.Id < cursor);
        return await query.OrderByDescending(t => t.Id).Take(limit).ToListAsync();
    }

    /// <summary>The transition filters, unordered and untracked: the base of the paged query and the export.</summary>
    public static IQueryable<TransitionRow> TransitionsQuery(ConWatchDbContext db,
        Guid? subjectId, DateTime? fromUtc, DateTime? toUtc,
        string? subjectName = null, ScopeFilter scope = ScopeFilter.All)
    {
        IQueryable<TransitionRow> query = Filtered(db, subjectId, subjectName, scope);
        if (fromUtc is { } from)
            query = query.Where(t => t.TimestampUtc >= from);
        if (toUtc is { } to)
            query = query.Where(t => t.TimestampUtc <= to);
        return query;
    }

    /// <summary>
    /// Each matching subject's latest transition strictly before <paramref name="fromUtc"/>, newest
    /// first: the state it was already in when the window opened. Same subject/name/scope filters
    /// as <see cref="TransitionsAsync"/>; not limited by any page size.
    /// </summary>
    public static async Task<List<TransitionRow>> OpeningTransitionsAsync(ConWatchDbContext db,
        Guid? subjectId, DateTime fromUtc, string? subjectName = null, ScopeFilter scope = ScopeFilter.All)
    {
        var latestIds = LatestTransitionIdsPerSubject(db, fromUtc);
        return await Filtered(db, subjectId, subjectName, scope)
            .Where(t => latestIds.Contains(t.Id))
            .OrderByDescending(t => t.Id).ToListAsync();
    }

    private static IQueryable<TransitionRow> Filtered(ConWatchDbContext db,
        Guid? subjectId, string? subjectName, ScopeFilter scope)
    {
        IQueryable<TransitionRow> query = db.Transitions.AsNoTracking();
        if (subjectId is { } sid)
            query = query.Where(t => t.SubjectId == sid);
        if (subjectName is { Length: > 0 })
            query = query.Where(t => t.SubjectName == subjectName);
        if (scope != ScopeFilter.All)
        {
            var localIds = db.Targets.Where(t => t.Scope == TargetScope.Local).Select(t => t.Id);
            var knownIds = db.Targets.Select(t => t.Id);
            // A Target subject that is no longer configured ("Unknown") is on neither side.
            query = scope == ScopeFilter.Local
                ? query.Where(t => localIds.Contains(t.SubjectId))
                : query.Where(t => t.SubjectKind != "Target" ||
                                   (knownIds.Contains(t.SubjectId) && !localIds.Contains(t.SubjectId)));
        }
        return query;
    }

    /// <summary>
    /// Picks the finest resolution whose retention window still contains
    /// <paramref name="fromUtc"/> under the <paramref name="effective"/> retention (the Settings
    /// table over appsettings); explicit requests pass through.
    /// </summary>
    public static string ResolveResolution(string requested, DateTime fromUtc, DateTime nowUtc,
        RetentionWindows effective)
    {
        if (!requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return requested.ToLowerInvariant() switch
            {
                "minute" => RollupResolution.Minute,
                "hour" => RollupResolution.Hour,
                "day" => RollupResolution.Day,
                _ => throw new ArgumentException($"Unknown resolution '{requested}'."),
            };
        }
        if (fromUtc >= nowUtc.AddDays(-effective.MinuteDays))
            return RollupResolution.Minute;
        if (fromUtc >= nowUtc.AddMonths(-effective.HourlyMonths))
            return RollupResolution.Hour;
        return RollupResolution.Day;
    }

    /// <summary>Rollup points for one target in ascending period order.</summary>
    public static Task<List<RollupRow>> RollupsAsync(ConWatchDbContext db,
        Guid targetId, DateTime fromUtc, DateTime toUtc, string resolution) =>
        RollupsQuery(db, targetId, resolution, fromUtc, toUtc)
            .OrderBy(r => r.PeriodStartUtc)
            .ToListAsync();

    /// <summary>Rollups filtered by optional target, resolution (empty means any) and period-start window; unordered, untracked.</summary>
    public static IQueryable<RollupRow> RollupsQuery(ConWatchDbContext db,
        Guid? targetId, string? resolution, DateTime? fromUtc, DateTime? toUtc)
    {
        IQueryable<RollupRow> query = db.Rollups.AsNoTracking();
        if (targetId is { } tid)
            query = query.Where(r => r.TargetId == tid);
        if (resolution is { Length: > 0 })
            query = query.Where(r => r.Resolution == resolution);
        if (fromUtc is { } from)
            query = query.Where(r => r.PeriodStartUtc >= from);
        if (toUtc is { } to)
            query = query.Where(r => r.PeriodStartUtc <= to);
        return query;
    }

    /// <summary>Glitches filtered by optional target, start window and scope; unordered, untracked.</summary>
    public static IQueryable<GlitchRow> GlitchesQuery(ConWatchDbContext db,
        Guid? targetId, DateTime? fromUtc, DateTime? toUtc, ScopeFilter scope = ScopeFilter.All)
    {
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
            var knownIds = db.Targets.Select(t => t.Id);
            // Internet-class glitches carry the verdict subject id (never a target id) and stay
            // on the internet side; a target-class glitch of a deleted target is on neither.
            query = scope == ScopeFilter.Local
                ? query.Where(g => localIds.Contains(g.TargetId))
                : query.Where(g => g.Class == (int)GlitchClass.Internet || (knownIds.Contains(g.TargetId) && !localIds.Contains(g.TargetId)));
        }
        return query;
    }

    /// <summary>Glitches newest-first (by start, then id), optionally for one target and window.</summary>
    public static Task<List<GlitchRow>> GlitchesAsync(ConWatchDbContext db,
        Guid? targetId, DateTime? fromUtc, DateTime? toUtc, int limit,
        ScopeFilter scope = ScopeFilter.All)
    {
        limit = Math.Clamp(limit, 1, 1000);
        IQueryable<GlitchRow> query = GlitchesQuery(db, targetId, fromUtc, toUtc, scope);
        return query.OrderByDescending(g => g.StartUtc).ThenByDescending(g => g.Id)
            .Take(limit).ToListAsync();
    }

    /// <summary>
    /// The id of each subject's most recent transition. Every retention and
    /// prune path must keep these rows: warm restart seeds the presumed state
    /// from them, however old they are. With <paramref name="beforeUtc"/>: each subject's latest
    /// transition strictly before that instant (a subject with none is absent).
    /// </summary>
    public static IQueryable<long> LatestTransitionIdsPerSubject(ConWatchDbContext db, DateTime? beforeUtc = null)
    {
        IQueryable<TransitionRow> rows = db.Transitions;
        if (beforeUtc is { } cutoff)
            rows = rows.Where(t => t.TimestampUtc < cutoff);
        return rows.GroupBy(t => t.SubjectId).Select(g => g.Max(t => t.Id));
    }
}
