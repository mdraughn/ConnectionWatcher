using ConCore.Targets;
using ConEngine.Configuration;
using ConPersistence.Dto;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConPersistence;

/// <summary>
/// Reads and seeds the monitoring configuration tables, and maps rows to the
/// engine's WatcherOptions shape (name references + stable ids).
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
    /// </summary>
    public async Task SeedAsync(WatcherOptions? fromAppSettings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        db.Profiles.Add(FromProfile(PingProfile.Internal));
        db.Profiles.Add(FromProfile(PingProfile.External));
        await db.SaveChangesAsync();

        if (fromAppSettings is null)
            return;

        var validation = new WatcherOptionsValidator().Validate(null, fromAppSettings);
        if (validation.Failed)
        {
            _log.LogWarning("Skipping appsettings config seed; validation failed: {Failures}",
                validation.FailureMessage);
            return;
        }

        foreach (var (name, config) in fromAppSettings.Profiles)
        {
            if (name.Equals("Internal", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("External", StringComparison.OrdinalIgnoreCase))
                continue; // builtins already seeded; file overrides not applied at seed
            db.Profiles.Add(FromProfile(config.ToProfile(name)));
        }
        await db.SaveChangesAsync();

        var profileIdsByName = await db.Profiles
            .ToDictionaryAsync(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var targetIdsByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in fromAppSettings.Targets)
        {
            var id = t.Id ?? Guid.NewGuid();
            targetIdsByName[t.Name] = id;
            db.Targets.Add(new TargetRow
            {
                Id = id, Name = t.Name, Address = t.Address,
                ProfileId = profileIdsByName[t.Profile], Enabled = t.Enabled,
                Operator = t.Operator,
            });
        }
        await db.SaveChangesAsync();

        // Second pass for parents (order-independent).
        foreach (var t in fromAppSettings.Targets.Where(t => t.Parent is { Length: > 0 }))
        {
            var row = await db.Targets.SingleAsync(r => r.Id == targetIdsByName[t.Name]);
            row.ParentId = targetIdsByName[t.Parent!];
        }

        foreach (var p in fromAppSettings.Pools)
        {
            var poolId = p.Id ?? Guid.NewGuid();
            db.Pools.Add(new PoolRow
            {
                Id = poolId, Name = p.Name,
                ParentId = p.Parent is { Length: > 0 } parent ? targetIdsByName[parent] : null,
                Members = p.Members.Select((m, i) => new PoolMemberRow
                {
                    PoolId = poolId, TargetId = targetIdsByName[m], MemberIndex = i,
                }).ToList(),
            });
        }
        await db.SaveChangesAsync();
        _log.LogInformation("Seeded configuration: {Targets} targets, {Pools} pools",
            fromAppSettings.Targets.Count, fromAppSettings.Pools.Count);
    }

    /// <summary>Maps config rows to engine options (stable ids, name references).</summary>
    public async Task<WatcherOptions> LoadWatcherOptionsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await LoadWatcherOptionsAsync(db);
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
        {
            options.Profiles[p.Name] = new PingProfileConfig
            {
                NormalIntervalSeconds = p.NormalIntervalSeconds,
                PingTimeoutSeconds = p.PingTimeoutSeconds,
                BurstIntervalSeconds = p.BurstIntervalSeconds,
                BurstMaxDurationSeconds = p.BurstMaxDurationSeconds,
                HistorySize = p.HistorySize,
                MinUsableSample = p.MinUsableSample,
                RespondingThreshold = p.RespondingThreshold,
                FalteringThreshold = p.FalteringThreshold,
                DownThreshold = p.DownThreshold,
                RecoveringThreshold = p.RecoveringThreshold,
            };
        }
        foreach (var t in targets)
        {
            options.Targets.Add(new TargetConfig
            {
                Id = t.Id, Name = t.Name, Address = t.Address,
                Parent = t.ParentId is { } pid ? targetNamesById[pid] : null,
                Profile = profileNamesById[t.ProfileId], Enabled = t.Enabled,
                Operator = t.Operator,
            });
        }
        foreach (var p in pools)
        {
            options.Pools.Add(new PoolConfig
            {
                Id = p.Id, Name = p.Name,
                Parent = p.ParentId is { } pid ? targetNamesById[pid] : null,
                Members = p.Members.OrderBy(m => m.MemberIndex)
                    .Select(m => targetNamesById[m.TargetId]).ToList(),
            });
        }
        return options;
    }

    /// <summary>Whole-config document for backup/transfer.</summary>
    public async Task<ConfigDocument> ExportAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var options = await LoadWatcherOptionsAsync(db);
        var profiles = await db.Profiles.AsNoTracking().ToListAsync();
        return new ConfigDocument
        {
            Profiles = profiles.Select(p => new ProfileDto
            {
                Id = p.Id, Name = p.Name,
                NormalIntervalSeconds = p.NormalIntervalSeconds,
                PingTimeoutSeconds = p.PingTimeoutSeconds,
                BurstIntervalSeconds = p.BurstIntervalSeconds,
                BurstMaxDurationSeconds = p.BurstMaxDurationSeconds,
                HistorySize = p.HistorySize,
                MinUsableSample = p.MinUsableSample,
                RespondingThreshold = p.RespondingThreshold,
                FalteringThreshold = p.FalteringThreshold,
                DownThreshold = p.DownThreshold,
                RecoveringThreshold = p.RecoveringThreshold,
            }).ToList(),
            Targets = options.Targets.Select(t => new TargetDto
            {
                Id = t.Id, Name = t.Name, Address = t.Address,
                Parent = t.Parent, Profile = t.Profile, Enabled = t.Enabled,
                Operator = t.Operator,
            }).ToList(),
            Pools = options.Pools.Select(p => new PoolDto
            {
                Id = p.Id, Name = p.Name, Members = p.Members, Parent = p.Parent,
            }).ToList(),
        };
    }

    private static ProfileRow FromProfile(PingProfile p) => new()
    {
        Id = Guid.NewGuid(),
        Name = p.Name,
        NormalIntervalSeconds = p.NormalInterval.TotalSeconds,
        PingTimeoutSeconds = p.PingTimeout.TotalSeconds,
        BurstIntervalSeconds = p.BurstInterval.TotalSeconds,
        BurstMaxDurationSeconds = p.BurstMaxDuration.TotalSeconds,
        HistorySize = p.HistorySize,
        MinUsableSample = p.MinUsableSample,
        RespondingThreshold = p.RespondingThreshold,
        FalteringThreshold = p.FalteringThreshold,
        DownThreshold = p.DownThreshold,
        RecoveringThreshold = p.RecoveringThreshold,
    };
}
