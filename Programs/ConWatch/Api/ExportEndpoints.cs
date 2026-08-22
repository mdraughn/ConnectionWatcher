using System.Text;
using ConPersistence;
using ConPersistence.Export;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Api;

/// <summary>Bulk measurement-data export: CSV or JSON, no row caps.</summary>
public static class ExportEndpoints
{
    public static void MapExport(this RouteGroupBuilder api)
    {
        api.MapGet("/export/transitions", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            TimeProvider time, string format = "csv", Guid? subjectId = null,
            DateTime? from = null, DateTime? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            await using var db = await dbFactory.CreateDbContextAsync();
            IQueryable<ConPersistence.Entities.TransitionRow> query = db.Transitions.AsNoTracking();
            if (subjectId is { } sid) query = query.Where(t => t.SubjectId == sid);
            if (from is { } f) query = query.Where(t => t.TimestampUtc >= f.ToUniversalTime());
            if (to is { } u) query = query.Where(t => t.TimestampUtc <= u.ToUniversalTime());
            var rows = await query.OrderBy(t => t.Id).ToListAsync();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                return Results.Json(rows, contentType: "application/json");
            return Csv(CsvExport.TransitionLines(rows), $"conwatch-transitions-{stamp}.csv");
        });

        api.MapGet("/export/rollups", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            TimeProvider time, string format = "csv", Guid? targetId = null,
            string? resolution = null, DateTime? from = null, DateTime? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            await using var db = await dbFactory.CreateDbContextAsync();
            IQueryable<ConPersistence.Entities.RollupRow> query = db.Rollups.AsNoTracking();
            if (targetId is { } tid) query = query.Where(r => r.TargetId == tid);
            if (resolution is { Length: > 0 }) query = query.Where(r => r.Resolution == resolution);
            if (from is { } f) query = query.Where(r => r.PeriodStartUtc >= f.ToUniversalTime());
            if (to is { } u) query = query.Where(r => r.PeriodStartUtc <= u.ToUniversalTime());
            var rows = await query.OrderBy(r => r.TargetId).ThenBy(r => r.PeriodStartUtc).ToListAsync();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                return Results.Json(rows, contentType: "application/json");
            return Csv(CsvExport.RollupLines(rows), $"conwatch-rollups-{stamp}.csv");
        });
    }

    private static bool IsValidFormat(string format) =>
        format.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
        format.Equals("json", StringComparison.OrdinalIgnoreCase);

    private static IResult BadFormat() =>
        Results.BadRequest(new { errors = new[] { "format must be 'csv' or 'json'." } });

    private static IResult Csv(IEnumerable<string> lines, string fileName)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        return Results.File(bytes, "text/csv", fileName);
    }
}
