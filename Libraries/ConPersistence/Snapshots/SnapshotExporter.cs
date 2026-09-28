using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Snapshots;

/// <summary>
/// Builds the <see cref="Snapshot"/> served by GET /api/v1/export/snapshot (and,
/// with secrets, the take-over channel); <see cref="SnapshotImporter"/> is its inverse.
/// Every part is read inside one read snapshot, so an import or recorder write
/// committing mid-export cannot leave settings, config and measurements from
/// different moments side by side.
/// </summary>
public static class SnapshotExporter
{
    public static async Task<Snapshot> ExportAsync(IDbContextFactory<ConWatchDbContext> dbFactory,
        ConfigStore configStore, TimeProvider time, bool includeSecrets, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var readSnapshot = await ConfigStore.BeginReadSnapshotAsync(db);

        var allSettings = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        foreach (var key in SettingKeys.PerNodeKeys)
            allSettings.Remove(key); // node identity never travels
        var redacted = includeSecrets
            ? new List<string>()
            : SettingKeys.SecretKeys.Where(key => allSettings.Remove(key)).ToList();

        var snapshot = new Snapshot
        {
            ExportedUtc = time.GetUtcNow(),
            Config = await configStore.ExportAsync(db),
            Settings = allSettings,
            RedactedSettings = redacted,
            Transitions = await db.Transitions.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct),
            Rollups = await db.Rollups.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct),
            Glitches = await db.Glitches.AsNoTracking().OrderBy(g => g.Id).ToListAsync(ct),
        };
        await readSnapshot.CommitAsync(ct);
        return snapshot;
    }
}
