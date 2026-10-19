using ConPersistence;
using ConPersistence.Queries;
using ConPersistence.Retention;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConWatch.Api;

/// <summary>Query endpoints for the transition log and rollups.</summary>
public static class HistoryEndpoints
{
    public static void MapHistory(this RouteGroupBuilder api)
    {
        api.MapGet("/transitions", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            Guid? subjectId, string? subjectName, string? from, string? to, string? scope,
            int limit = 100, long? before = null, bool opening = false) =>
        {
            if (!ScopeFilters.TryParse(scope, out var scopeFilter))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null,
                    "'scope' must be all, internet or local.");
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.TransitionsAsync(db, subjectId,
                fromUtc, toUtc, limit, before, subjectName, scopeFilter);
            var localIds = await HistoryQueries.LocalTargetIdsAsync(db);
            var resolverIds = await HistoryQueries.ResolverTargetIdsAsync(db);
            var knownIds = await HistoryQueries.KnownTargetIdsAsync(db);
            var items = new List<TransitionDto>();
            // Opt-in, first page only, and only with a lower bound: each subject's latest row
            // before `from` leads the list, marked. They sit outside `limit` and the paging cursor.
            if (opening && before is null && fromUtc is { } openFrom)
            {
                var openingRows = await HistoryQueries.OpeningTransitionsAsync(db, subjectId,
                    openFrom, subjectName, scopeFilter);
                items.AddRange(openingRows.Select(r =>
                    TransitionDto.From(r, localIds, resolverIds, knownIds) with { Opening = true }));
            }
            items.AddRange(rows.Select(r => TransitionDto.From(r, localIds, resolverIds, knownIds)));
            long? nextBefore = rows.Count == Math.Clamp(limit, 1, 1000) ? rows.Last().Id : null;
            return Results.Ok(new { Items = items, NextBefore = nextBefore });
        });

        api.MapGet("/rollups/{targetId:guid}", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            IOptions<PersistenceOptions> options, SettingsStore settings, TimeProvider time,
            Guid targetId, string? from, string? to, string resolution = "auto") =>
        {
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            if (fromUtc is null || toUtc is null)
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "'from' and 'to' are required.");

            string resolved;
            try
            {
                resolved = HistoryQueries.ResolveResolution(resolution, fromUtc.Value,
                    time.GetUtcNow().UtcDateTime, await RetentionWindows.EffectiveAsync(settings, options.Value));
            }
            catch (ArgumentException ex)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, ex.Message);
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.RollupsAsync(db, targetId,
                fromUtc.Value, toUtc.Value, resolved);
            return Results.Ok(new
            {
                Resolution = resolved,
                Points = rows.Select(r => new
                {
                    PeriodStart = new DateTimeOffset(r.PeriodStartUtc),
                    r.PingCount, r.SuccessCount, r.RttAvg, r.RttMin, r.RttMax,
                }).ToList(),
            });
        });
    }
}
