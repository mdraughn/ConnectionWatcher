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
            var tags = await ScopeTags.LoadAsync(db);

            return Results.Ok(new { Items = rows.Select(g => GlitchDto.From(g, tags)).ToList() });
        });
    }
}
