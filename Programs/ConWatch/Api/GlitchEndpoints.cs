using ConPersistence;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Api;

/// <summary>Query endpoint for recorded glitches (open, read-only).</summary>
public static class GlitchEndpoints
{
    public static void MapGlitches(this RouteGroupBuilder api)
    {
        api.MapGet("/glitches", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            Guid? targetId, string? from, string? to, int limit = 100) =>
        {
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.GlitchesAsync(db, targetId, fromUtc, toUtc, limit);

            return Results.Ok(new
            {
                Items = rows.Select(g => new
                {
                    g.Id, g.TargetId, g.TargetName,
                    Class = g.Class == 1 ? "Internet" : "Target",
                    StartUtc = new DateTimeOffset(DateTime.SpecifyKind(g.StartUtc, DateTimeKind.Utc)),
                    EndUtc = new DateTimeOffset(DateTime.SpecifyKind(g.EndUtc, DateTimeKind.Utc)),
                    g.LostCount, g.SampleCount, g.Detail,
                }).ToList(),
            });
        });
    }
}
