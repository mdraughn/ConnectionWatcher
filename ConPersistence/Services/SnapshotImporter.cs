using ConPersistence.Dto;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConPersistence;

/// <summary>
/// Rebuilds a local database from a <see cref="Snapshot"/> (Route A of
/// real-data testing: fetch the open snapshot API, import, boot the app on
/// the result). Replace-all semantics: existing config and measurement data
/// in the target database are dropped first.
/// </summary>
public static class SnapshotImporter
{
    /// <summary>Creates (or migrates) the SQLite file at dbPath and imports into it.</summary>
    public static async Task ImportToFileAsync(string dbPath, Snapshot snapshot, CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite($"Data Source={dbPath}").Options;
        IDbContextFactory<ConWatchDbContext> factory = new SimpleFactory(options);
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.MigrateAsync(ct);
        }
        await ImportAsync(factory, snapshot, ct);
    }

    public static async Task ImportAsync(IDbContextFactory<ConWatchDbContext> dbFactory,
        Snapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.SchemaVersion != 1)
            throw new InvalidOperationException(
                $"Snapshot schema version {snapshot.SchemaVersion} is not supported (expected 1).");

        // Config first (validated, atomic, ID-preserving) — the measurement
        // rows below reference these target GUIDs.
        var store = new ConfigStore(dbFactory);
        var configService = new ConfigService(dbFactory, store, NullLogger<ConfigService>.Instance);
        var result = await configService.ImportAsync(snapshot.Config);
        if (!result.Success)
            throw new InvalidOperationException(
                "Snapshot config failed validation: " + string.Join("; ", result.Errors ?? []));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

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
            db.Settings.Add(new Entities.SettingRow { Key = key, Value = value });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private sealed class SimpleFactory(DbContextOptions<ConWatchDbContext> options)
        : IDbContextFactory<ConWatchDbContext>
    {
        public ConWatchDbContext CreateDbContext() => new(options);
    }
}
