using System.Text;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Export;
using ConPersistence.Queries;
using ConPersistence.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Api;

/// <summary>Bulk measurement-data export: CSV or JSON, no row caps. Rows stream
/// from the database reader to the response body; nothing is materialized.</summary>
public static class ExportEndpoints
{
    public static void MapExport(this RouteGroupBuilder api)
    {
        api.MapGet("/export/transitions", (HttpContext http,
            IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format = "csv",
            Guid? subjectId = null, string? from = null, string? to = null) =>
            ExportAsync<TransitionRow>(http, dbFactory, time, format, from, to, "transitions",
                CsvExport.TransitionHeader, CsvExport.TransitionLine,
                (db, fromUtc, toUtc) => HistoryQueries.TransitionsQuery(db, subjectId, fromUtc, toUtc).OrderBy(t => t.Id)));

        api.MapGet("/export/rollups", (HttpContext http,
            IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format = "csv",
            Guid? targetId = null, string? resolution = null, string? from = null, string? to = null) =>
            ExportAsync<RollupRow>(http, dbFactory, time, format, from, to, "rollups",
                CsvExport.RollupHeader, CsvExport.RollupLine,
                (db, fromUtc, toUtc) => HistoryQueries.RollupsQuery(db, targetId, resolution, fromUtc, toUtc)
                    .OrderBy(r => r.TargetId).ThenBy(r => r.PeriodStartUtc)));

        // Route A of real-data testing: the complete sanitized state in one
        // document, retrievable without admin. Secrets (SettingKeys.SecretKeys)
        // are omitted and named in redactedSettings.
        api.MapGet("/export/snapshot", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            ConfigStore configStore, TimeProvider time) =>
                Results.Ok(await SnapshotExporter.ExportAsync(dbFactory, configStore, time, includeSecrets: false)));

        // Take-over channel: the SAME document with secrets included, guarded
        // by the source's admin password verified inline (single shot, no
        // cookie dance; open while the gate is disabled, like the rest of the
        // locked surface). Transport policy is the REQUESTER's job — the spec
        // explains why a server behind a TLS-terminating proxy cannot enforce
        // it: docs/superpowers/specs/2026-09-12-clone-takeover-design.md.
        api.MapPost("/export/snapshot/full", async (FullSnapshotRequest request,
            ConWatch.Auth.AdminGate gate, IDbContextFactory<ConWatchDbContext> dbFactory,
            ConfigStore configStore, TimeProvider time) =>
        {
            if (!await gate.VerifyOrDelayAsync(request.Password))
                return Results.Unauthorized();
            return Results.Ok(await SnapshotExporter.ExportAsync(dbFactory, configStore, time, includeSecrets: true));
        });

        api.MapGet("/export/glitches", (HttpContext http,
            IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format = "csv",
            Guid? targetId = null, string? from = null, string? to = null) =>
            ExportAsync<GlitchRow>(http, dbFactory, time, format, from, to, "glitches",
                CsvExport.GlitchHeader, CsvExport.GlitchLine,
                (db, fromUtc, toUtc) => HistoryQueries.GlitchesQuery(db, targetId, fromUtc, toUtc).OrderBy(g => g.Id)));
    }

    public record FullSnapshotRequest(string? Password);

    /// <summary>The shared export skeleton: format check, range parse, response-owned context,
    /// then the ordered query streamed as JSON or CSV named conwatch-{kind}-{stamp}.csv.</summary>
    private static async Task<IResult> ExportAsync<T>(HttpContext http,
        IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format,
        string? from, string? to, string kind, string csvHeader, Func<T, string> csvLine,
        Func<ConWatchDbContext, DateTime?, DateTime?, IQueryable<T>> query)
    {
        if (!IsValidFormat(format))
            return BadFormat();
        if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
            return problem!;
        var db = await OpenForResponseAsync(http, dbFactory);
        var rows = query(db, fromUtc, toUtc).AsAsyncEnumerable();

        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
        return IsJson(format)
            ? Results.Json(rows, contentType: "application/json")
            : Csv(csvHeader, rows, csvLine, $"conwatch-{kind}-{stamp}.csv", http.RequestAborted);
    }

    private static bool IsValidFormat(string format) =>
        format.Equals("csv", StringComparison.OrdinalIgnoreCase) ||
        format.Equals("json", StringComparison.OrdinalIgnoreCase);

    private static IResult BadFormat() =>
        ApiResults.Error(StatusCodes.Status400BadRequest, null, "format must be 'csv' or 'json'.");

    /// <summary>The context outlives the handler: rows are read while the
    /// response body is written. The response disposes it when the request ends.</summary>
    private static async Task<ConWatchDbContext> OpenForResponseAsync(HttpContext http,
        IDbContextFactory<ConWatchDbContext> dbFactory)
    {
        var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
        http.Response.RegisterForDisposeAsync(db);
        return db;
    }

    private static bool IsJson(string format) =>
        format.Equals("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Header, then one line per row, each ending in '\n'; UTF-8, no BOM.
    /// A client disconnect ends the enumeration through <paramref name="cancellationToken"/>
    /// (the request's <see cref="HttpContext.RequestAborted"/>), passed via
    /// <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator"/>/<c>WithCancellation</c>.</summary>
    private static IResult Csv<T>(string header, IAsyncEnumerable<T> rows, Func<T, string> line,
        string fileName, CancellationToken cancellationToken) =>
        Results.Stream(async body =>
        {
            await using var writer = new StreamWriter(body,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
            await writer.WriteAsync(header);
            await writer.WriteAsync('\n');
            await foreach (var row in rows.WithCancellation(cancellationToken))
            {
                await writer.WriteAsync(line(row));
                await writer.WriteAsync('\n');
            }
        }, "text/csv", fileName);
}
