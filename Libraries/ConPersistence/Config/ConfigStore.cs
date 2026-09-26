using ConCore.Targets;
using ConEngine.Configuration;
using ConPersistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConPersistence.Config;

/// <summary>
/// Reads and seeds the monitoring configuration tables, maps rows to the
/// engine's WatcherOptions shape (name references + stable ids), and owns the
/// one routine that writes a whole ConfigDocument into the tables.
/// </summary>
public class ConfigStore(IDbContextFactory<ConWatchDbContext> dbFactory, ILogger<ConfigStore>? log = null)
{
    private readonly ILogger<ConfigStore> _log = log ?? NullLogger<ConfigStore>.Instance;

    /// <summary>True when no profiles exist (first boot).</summary>
    public async Task<bool> IsEmptyAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return !await db.Profiles.AnyAsync();
    }

    /// <summary>
    /// Seeds built-in profiles and, when valid file config is supplied, its
    /// custom profiles, targets, and pools. Invalid file config is skipped.
    /// Goes through the same document write as import, so probes and
    /// operators are normalized identically.
    /// </summary>
    public async Task SeedAsync(WatcherOptions? fromAppSettings)
    {
        var doc = new ConfigDocument
        {
            Profiles = [PingProfile.Internal.ToDto(), PingProfile.External.ToDto()],
        };

        if (fromAppSettings is not null)
        {
            var validation = new WatcherOptionsValidator().Validate(null, fromAppSettings);
            if (validation.Failed)
            {
                _log.LogWarning("Skipping appsettings config seed; validation failed: {Failures}",
                    validation.FailureMessage);
            }
            else
            {
                foreach (var (name, config) in fromAppSettings.Profiles)
                {
                    if (name.Equals("Internal", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("External", StringComparison.OrdinalIgnoreCase))
                        continue; // builtins already present; file overrides not applied at seed
                    doc.Profiles.Add(config.ToProfile(name).ToDto());
                }
                doc.Targets.AddRange(fromAppSettings.Targets.Select(t => t.ToDto()));
                doc.Pools.AddRange(fromAppSettings.Pools.Select(p => p.ToDto()));
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        await WriteDocumentAsync(db, doc);
        if (doc.Targets.Count > 0 || doc.Pools.Count > 0)
            _log.LogInformation("Seeded configuration: {Targets} targets, {Pools} pools",
                doc.Targets.Count, doc.Pools.Count);
    }

    /// <summary>
    /// Replaces the configuration tables with <paramref name="doc"/>: profiles,
    /// then targets (ids preserved when present), then parents by name, then
    /// pools with ordered members. The caller owns validation and the
    /// transaction; this only knows the table shape.
    /// </summary>
    internal static async Task WriteDocumentAsync(ConWatchDbContext db, ConfigDocument doc)
    {
        db.PoolMembers.RemoveRange(db.PoolMembers);
        db.Pools.RemoveRange(db.Pools);
        db.Targets.RemoveRange(db.Targets);
        db.Profiles.RemoveRange(db.Profiles);
        await db.SaveChangesAsync();

        var profileIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.Profiles)
        {
            var id = p.Id ?? Guid.NewGuid();
            profileIds[p.Name] = id;
            db.Profiles.Add(p.ToRow(id));
        }

        var rowsByName = new Dictionary<string, TargetRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in doc.Targets)
        {
            var row = new TargetRow { Id = t.Id ?? Guid.NewGuid() }
                .CopyFrom(t, profileIds[t.Profile], parentId: null);
            rowsByName[t.Name] = row;
            db.Targets.Add(row);
        }
        await db.SaveChangesAsync();

        // Second pass for parents (order-independent). The rows are tracked,
        // so this is an in-memory assignment, not one query per target.
        foreach (var t in doc.Targets.Where(t => t.Parent is { Length: > 0 }))
            rowsByName[t.Name].ParentId = rowsByName[t.Parent!].Id;

        foreach (var p in doc.Pools)
        {
            var poolId = p.Id ?? Guid.NewGuid();
            db.Pools.Add(new PoolRow
            {
                Id = poolId, Name = p.Name,
                ParentId = p.Parent is { Length: > 0 } parent ? rowsByName[parent].Id : null,
                Members = p.Members.Select((m, i) => new PoolMemberRow
                {
                    PoolId = poolId, TargetId = rowsByName[m].Id, MemberIndex = i,
                }).ToList(),
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Maps config rows to engine options (stable ids, name references), from one snapshot.</summary>
    public async Task<WatcherOptions> LoadWatcherOptionsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var snapshot = await BeginReadSnapshotAsync(db);
        var options = await LoadWatcherOptionsAsync(db);
        await snapshot.CommitAsync();
        return options;
    }

    /// <summary>Same, over a caller-supplied context (used mid-transaction by ConfigService).</summary>
    public async Task<WatcherOptions> LoadWatcherOptionsAsync(ConWatchDbContext db)
    {
        var profiles = await db.Profiles.AsNoTracking().ToListAsync();
        var targets = await db.Targets.AsNoTracking().ToListAsync();
        var pools = await db.Pools.AsNoTracking().Include(p => p.Members).ToListAsync();

        var profileNamesById = profiles.ToDictionary(p => p.Id, p => p.Name);
        var targetNamesById = targets.ToDictionary(t => t.Id, t => t.Name);

        var options = new WatcherOptions();
        foreach (var p in profiles)
            options.Profiles[p.Name] = p.ToConfig();
        foreach (var t in targets)
            options.Targets.Add(t.ToConfig(
                t.ParentId is { } pid ? targetNamesById[pid] : null,
                profileNamesById[t.ProfileId]));
        foreach (var p in pools)
            options.Pools.Add(p.ToConfig(
                p.ParentId is { } pid ? targetNamesById[pid] : null,
                p.Members.OrderBy(m => m.MemberIndex).Select(m => targetNamesById[m.TargetId]).ToList()));
        return options;
    }

    /// <summary>Whole-config document for backup/transfer, from one snapshot.</summary>
    public async Task<ConfigDocument> ExportAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var snapshot = await BeginReadSnapshotAsync(db);
        var doc = await ExportAsync(db);
        await snapshot.CommitAsync();
        return doc;
    }

    /// <summary>Same, over a caller-supplied context (used mid-transaction by ConfigService).</summary>
    internal async Task<ConfigDocument> ExportAsync(ConWatchDbContext db)
    {
        var options = await LoadWatcherOptionsAsync(db);
        var profiles = await db.Profiles.AsNoTracking().ToListAsync();
        return new ConfigDocument
        {
            Profiles = profiles.Select(p => p.ToDto()).ToList(),
            Targets = options.Targets.Select(t => t.ToDto()).ToList(),
            Pools = options.Pools.Select(p => p.ToDto()).ToList(),
        };
    }

    /// <summary>
    /// A deferred transaction ("BEGIN", not EF's default "BEGIN IMMEDIATE"):
    /// it takes no write lock, and in WAL mode its first read pins one
    /// snapshot until it ends, so several queries see one state of the
    /// database even while an import commits between them.
    /// </summary>
    private static async Task<SqliteTransaction> BeginReadSnapshotAsync(ConWatchDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(transaction);
        return transaction;
    }
}
