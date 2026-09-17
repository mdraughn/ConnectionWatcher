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
            string? from = null, string? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            IQueryable<ConPersistence.Entities.TransitionRow> query = db.Transitions.AsNoTracking();
            if (subjectId is { } sid) query = query.Where(t => t.SubjectId == sid);
            if (fromUtc is { } f) query = query.Where(t => t.TimestampUtc >= f);
            if (toUtc is { } u) query = query.Where(t => t.TimestampUtc <= u);
            var rows = await query.OrderBy(t => t.Id).ToListAsync();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                return Results.Json(rows, contentType: "application/json");
            return Csv(CsvExport.TransitionLines(rows), $"conwatch-transitions-{stamp}.csv");
        });

        api.MapGet("/export/rollups", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            TimeProvider time, string format = "csv", Guid? targetId = null,
            string? resolution = null, string? from = null, string? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            IQueryable<ConPersistence.Entities.RollupRow> query = db.Rollups.AsNoTracking();
            if (targetId is { } tid) query = query.Where(r => r.TargetId == tid);
            if (resolution is { Length: > 0 }) query = query.Where(r => r.Resolution == resolution);
            if (fromUtc is { } f) query = query.Where(r => r.PeriodStartUtc >= f);
            if (toUtc is { } u) query = query.Where(r => r.PeriodStartUtc <= u);
            var rows = await query.OrderBy(r => r.TargetId).ThenBy(r => r.PeriodStartUtc).ToListAsync();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                return Results.Json(rows, contentType: "application/json");
            return Csv(CsvExport.RollupLines(rows), $"conwatch-rollups-{stamp}.csv");
        });

        // Route A of real-data testing: the complete sanitized state in one
        // document, retrievable without admin. Secrets (SettingKeys.SecretKeys)
        // are omitted and named in redactedSettings.
        api.MapGet("/export/snapshot", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            ConfigStore configStore, SettingsStore settingsStore, TimeProvider time) =>
                Results.Ok(await BuildSnapshotAsync(dbFactory, configStore, settingsStore, time,
                    includeSecrets: false)));

        // Take-over channel: the SAME document with secrets included, guarded
        // by the source's admin password verified inline (single shot, no
        // cookie dance; open while the gate is disabled, like the rest of the
        // locked surface). Transport policy is the REQUESTER's job — the spec
        // explains why a server behind a TLS-terminating proxy cannot enforce
        // it: docs/superpowers/specs/2026-09-12-clone-takeover-design.md.
        api.MapPost("/export/snapshot/full", async (FullSnapshotRequest request,
            ConWatch.Auth.AdminGate gate, IDbContextFactory<ConWatchDbContext> dbFactory,
            ConfigStore configStore, SettingsStore settingsStore, TimeProvider time) =>
        {
            if (gate.IsEnabled && !gate.Verify(request.Password ?? ""))
            {
                await Task.Delay(1000); // same brute-force friction as login
                return Results.Unauthorized();
            }
            return Results.Ok(await BuildSnapshotAsync(dbFactory, configStore, settingsStore, time,
                includeSecrets: true));
        });

        api.MapGet("/export/glitches", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            TimeProvider time, string format = "csv", Guid? targetId = null,
            string? from = null, string? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            IQueryable<ConPersistence.Entities.GlitchRow> query = db.Glitches.AsNoTracking();
            if (targetId is { } tid) query = query.Where(g => g.TargetId == tid);
            if (fromUtc is { } f) query = query.Where(g => g.StartUtc >= f);
            if (toUtc is { } u) query = query.Where(g => g.StartUtc <= u);
            var rows = await query.OrderBy(g => g.Id).ToListAsync();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                return Results.Json(rows, contentType: "application/json");
            return Csv(CsvExport.GlitchLines(rows), $"conwatch-glitches-{stamp}.csv");
        });
    }

    public record FullSnapshotRequest(string? Password);

    private static async Task<ConPersistence.Dto.Snapshot> BuildSnapshotAsync(
        IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore configStore,
        SettingsStore settingsStore, TimeProvider time, bool includeSecrets)
    {
        var allSettings = await settingsStore.GetAllAsync();
        foreach (var key in SettingKeys.PerNodeKeys)
            allSettings.Remove(key); // node identity never travels
        var redacted = includeSecrets
            ? new List<string>()
            : SettingKeys.SecretKeys.Where(key => allSettings.Remove(key)).ToList();

        await using var db = await dbFactory.CreateDbContextAsync();
        return new ConPersistence.Dto.Snapshot
        {
            ExportedUtc = time.GetUtcNow(),
            Config = await configStore.ExportAsync(),
            Settings = allSettings,
            RedactedSettings = redacted,
            Transitions = await db.Transitions.AsNoTracking().OrderBy(t => t.Id).ToListAsync(),
            Rollups = await db.Rollups.AsNoTracking().OrderBy(r => r.Id).ToListAsync(),
            Glitches = await db.Glitches.AsNoTracking().OrderBy(g => g.Id).ToListAsync(),
        };
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
