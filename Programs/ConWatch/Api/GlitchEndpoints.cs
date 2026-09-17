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
            limit = Math.Clamp(limit, 1, 1000);
            await using var db = await dbFactory.CreateDbContextAsync();
            IQueryable<ConPersistence.Entities.GlitchRow> query = db.Glitches.AsNoTracking();
            if (targetId is { } tid) query = query.Where(g => g.TargetId == tid);
            if (fromUtc is { } f) query = query.Where(g => g.StartUtc >= f);
            if (toUtc is { } u) query = query.Where(g => g.StartUtc <= u);
            var rows = await query.OrderByDescending(g => g.StartUtc).Take(limit).ToListAsync();

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
