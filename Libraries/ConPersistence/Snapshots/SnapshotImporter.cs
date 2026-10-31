using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Snapshots;

/// <summary>
/// Rebuilds a local database from a <see cref="Snapshot"/> (Route A of
/// real-data testing: fetch the open snapshot API, import, boot the app on
/// the result). Replace-all semantics: existing config and measurement data
/// in the target database are dropped first.
/// </summary>
public static class SnapshotImporter
{
    /// <summary>Creates (or migrates) the SQLite file at dbPath and imports into it.</summary>
    internal static async Task ImportToFileAsync(string dbPath, Snapshot snapshot, CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        IDbContextFactory<ConWatchDbContext> factory = new SimpleFactory(options);
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
        }
        // The CLI/test route: no host is listening, so a private ConfigService is enough.
        await ImportAsync(factory, new ConfigService(factory, new ConfigStore(factory)), snapshot, ct);
    }

    /// <summary>
    /// Replace-all import. Config goes through <paramref name="configService"/>
    /// (validated, atomic, id-preserving) so its ConfigChanged reaches whoever
    /// listens to that instance — in the app, the host's rebuild.
    /// </summary>
    public static async Task ImportAsync(IDbContextFactory<ConWatchDbContext> dbFactory,
        ConfigService configService, Snapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.SchemaVersion != 1)
            throw new InvalidOperationException(
                $"Snapshot schema version {snapshot.SchemaVersion} is not supported (expected 1).");

        // Config first — the measurement rows below reference these target GUIDs.
        ConfigResult result;
        try
        {
            result = await configService.ImportAsync(snapshot.Config);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The config write is one transaction: whatever threw, it rolled back.
            throw new SnapshotImportException(configCommitted: false,
                "The snapshot's config could not be written: " + ex.Message, ex);
        }
        if (!result.Success)
            throw new InvalidOperationException(
                "Snapshot config failed validation: " + string.Join("; ", result.Errors ?? []));

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // Per-node keys are THIS node's identity (Engine.Enabled,
            // Clone.SourceUrl): keep the local values and ignore any that a
            // snapshot might carry — a take-over's final sync from a
            // just-disabled source must not import Enabled=false.
            var perNode = await db.Settings
                .Where(s => SettingKeys.PerNodeKeys.Contains(s.Key))
                .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

            db.Transitions.RemoveRange(db.Transitions);
            db.Rollups.RemoveRange(db.Rollups);
            db.Glitches.RemoveRange(db.Glitches);
            db.Settings.RemoveRange(db.Settings);
            await db.SaveChangesAsync(ct);

            // Preserve original row ids (pagination cursors key off them).
            db.Transitions.AddRange(snapshot.Transitions);
            db.Rollups.AddRange(snapshot.Rollups);
            db.Glitches.AddRange(snapshot.Glitches);
            foreach (var (key, value) in snapshot.Settings)
            {
                if (!SettingKeys.PerNodeKeys.Contains(key))
                    db.Settings.Add(new Entities.SettingRow { Key = key, Value = value });
            }
            foreach (var (key, value) in perNode)
                db.Settings.Add(new Entities.SettingRow { Key = key, Value = value });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The config has committed; the measurement data is partial or gone.
            throw new SnapshotImportException(configCommitted: true,
                "The snapshot's config was imported but its history could not be: " + ex.Message, ex);
        }
    }

    private sealed class SimpleFactory(DbContextOptions<ConWatchDbContext> options)
        : IDbContextFactory<ConWatchDbContext>
    {
        public ConWatchDbContext CreateDbContext() => new(options);
    }
}
