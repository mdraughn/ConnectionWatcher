using ConPersistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ConWatch.Api;

/// <summary>Query endpoints for the transition log and rollups.</summary>
public static class HistoryEndpoints
{
    public static void MapHistory(this RouteGroupBuilder api)
    {
        api.MapGet("/transitions", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            Guid? subjectId, string? subjectName, DateTime? from, DateTime? to,
            int limit = 100, long? before = null) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.TransitionsAsync(db, subjectId,
                from?.ToUniversalTime(), to?.ToUniversalTime(), limit, before, subjectName);
            var items = rows.Select(r => new
            {
                r.Id,
                Timestamp = new DateTimeOffset(DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc)),
                r.SubjectId, r.SubjectName, r.SubjectKind,
                From = r.FromState, To = r.ToState, r.Trigger,
                r.SuccessRate, r.SampleSize, r.BurstLossRate,
                r.AncestorId, r.Detail, r.Verdict, r.AvgRtt,
            }).ToList();
            long? nextBefore = rows.Count == Math.Clamp(limit, 1, 1000) ? rows.Last().Id : null;
            return Results.Ok(new { Items = items, NextBefore = nextBefore });
        });

        api.MapGet("/rollups/{targetId:guid}", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            IOptions<PersistenceOptions> options, TimeProvider time,
            Guid targetId, DateTime? from, DateTime? to, string resolution = "auto") =>
        {
            if (from is null || to is null)
                return Results.BadRequest(new { errors = new[] { "'from' and 'to' are required." } });

            string resolved;
            try
            {
                resolved = HistoryQueries.ResolveResolution(resolution,
                    from.Value.ToUniversalTime(), time.GetUtcNow().UtcDateTime, options.Value);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { errors = new[] { ex.Message } });
            }

            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.RollupsAsync(db, targetId,
                from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), resolved);
            return Results.Ok(new
            {
                Resolution = resolved,
                Points = rows.Select(r => new
                {
                    PeriodStart = new DateTimeOffset(DateTime.SpecifyKind(r.PeriodStartUtc, DateTimeKind.Utc)),
                    r.PingCount, r.SuccessCount, r.RttAvg, r.RttMin, r.RttMax,
                }).ToList(),
            });
        });
    }
}
