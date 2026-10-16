using ConPersistence;
using ConPersistence.Queries;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Api;

/// <summary>Query endpoint for recorded glitches (open, read-only).</summary>
public static class GlitchEndpoints
{
    public static void MapGlitches(this RouteGroupBuilder api)
    {
        api.MapGet("/glitches", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            Guid? targetId, string? from, string? to, string? scope, int limit = 100) =>
        {
            if (!ScopeFilters.TryParse(scope, out var scopeFilter))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null,
                    "'scope' must be all, internet or local.");
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.GlitchesAsync(db, targetId, fromUtc, toUtc, limit, scopeFilter);
            var localIds = await HistoryQueries.LocalTargetIdsAsync(db);
            var resolverIds = await HistoryQueries.ResolverTargetIdsAsync(db);
            var knownIds = await HistoryQueries.KnownTargetIdsAsync(db);

            return Results.Ok(new
            {
                Items = rows.Select(g => new
                {
                    g.Id, g.TargetId, g.TargetName,
                    Class = g.Class == 1 ? "Internet" : "Target",
                    StartUtc = new DateTimeOffset(g.StartUtc),
                    EndUtc = new DateTimeOffset(g.EndUtc),
                    g.LostCount, g.SampleCount, g.Detail,
                    Scope = HistoryQueries.ScopeOf(g.TargetId, g.Class == 1 ? "Verdict" : "Target", localIds, resolverIds, knownIds),
                }).ToList(),
            });
        });
    }
}
