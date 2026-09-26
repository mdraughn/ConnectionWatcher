using ConEngine.Configuration;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConPersistence.Config;

public enum ConfigErrorKind { None, Validation, NotFound, Conflict }

/// <summary>Outcome of a config mutation.</summary>
public record ConfigResult(bool Success, ConfigErrorKind Kind = ConfigErrorKind.None,
    IReadOnlyList<string>? Errors = null, Guid? Id = null)
{
    public static ConfigResult Ok(Guid? id = null) => new(true, Id: id);
    public static ConfigResult Fail(ConfigErrorKind kind, params string[] errors) =>
        new(false, kind, errors);
}

/// <summary>
/// The CRUD brain: every mutation is applied in a transaction, the resulting
/// whole config is validated with the engine's WatcherOptionsValidator, and
/// only valid states are committed. Successful commits raise ConfigChanged.
/// </summary>
public class ConfigService(IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore store,
    ILogger<ConfigService>? log = null, TimeProvider? timeProvider = null)
{
    private readonly ILogger<ConfigService> _log = log ?? NullLogger<ConfigService>.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private static readonly Dictionary<string, string> AddressChangeMarkers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // correction/repoint start a fresh data history; renumber continues it.
            ["correction"] = "HistoryReset",
            ["repoint"] = "HistoryReset",
            ["renumber"] = "AddressChanged",
        };

    /// <summary>Raised after any committed config change; the host debounces rebuilds.</summary>
    public event Action? ConfigChanged;

    public Task<ConfigResult> UpsertProfileAsync(ProfileDto dto) => MutateAsync(async db =>
    {
        ProfileRow row;
        if (dto.Id is { } id)
        {
            var existing = await db.Profiles.FindAsync(id);
            if (existing is null)
                return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No profile with id {id}.");
            row = existing;
        }
        else
        {
            row = new ProfileRow { Id = Guid.NewGuid() };
            db.Profiles.Add(row);
        }
        row.CopyFrom(dto);
        return ConfigResult.Ok(row.Id);
    });

    public Task<ConfigResult> DeleteProfileAsync(Guid id) => MutateAsync(async db =>
    {
        var row = await db.Profiles.FindAsync(id);
        if (row is null)
            return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No profile with id {id}.");
        var users = await db.Targets.Where(t => t.ProfileId == id).Select(t => t.Name).ToListAsync();
        if (users.Count > 0)
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                $"Profile '{row.Name}' is used by: {string.Join(", ", users)}.");
        db.Profiles.Remove(row);
        return ConfigResult.Ok(id);
    });

    public Task<ConfigResult> UpsertTargetAsync(TargetDto dto, string? addressChange = null) => MutateAsync(async db =>
    {
        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.Name == dto.Profile);
        if (profile is null)
            return ConfigResult.Fail(ConfigErrorKind.Validation, $"Unknown profile '{dto.Profile}'.");

        Guid? parentId = null;
        if (dto.Parent is { Length: > 0 } parentName)
        {
            var parent = await db.Targets.SingleOrDefaultAsync(t => t.Name == parentName);
            if (parent is null)
                return ConfigResult.Fail(ConfigErrorKind.Validation, $"Unknown parent target '{parentName}'.");
            parentId = parent.Id;
        }

        TargetRow row;
        if (dto.Id is { } id)
        {
            var existing = await db.Targets.FindAsync(id);
            if (existing is null)
                return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No target with id {id}.");
            row = existing;

            var oldAddress = row.Address;
            var newAddress = ConfigMapping.NormalizeAddress(dto.Address);
            if (!string.Equals(oldAddress, newAddress, StringComparison.OrdinalIgnoreCase))
            {
                // The user must say what an address edit MEANS: correcting a
                // mistake / probing a different interface (fresh history) or a
                // renumbered interface (continuous history).
                if (addressChange is null || !AddressChangeMarkers.TryGetValue(addressChange, out var trigger))
                    return ConfigResult.Fail(ConfigErrorKind.Validation,
                        "Address changed; specify addressChange as one of: correction, repoint, renumber.");

                db.Transitions.Add(new TransitionRow
                {
                    TimestampUtc = _time.GetUtcNow().UtcDateTime,
                    SubjectId = row.Id,
                    SubjectName = dto.Name,
                    SubjectKind = "Target",
                    FromState = "Starting",
                    ToState = "Starting",
                    Trigger = trigger,
                    Detail = $"address changed {oldAddress} → {newAddress} ({addressChange.ToLowerInvariant()})",
                });
            }
        }
        else
        {
            row = new TargetRow { Id = Guid.NewGuid() };
            db.Targets.Add(row);
        }
        row.CopyFrom(dto, profile.Id, parentId);
        return ConfigResult.Ok(row.Id);
    });

    /// <summary>
    /// Slides a new target into the tree above the given children: the new
    /// target takes their (shared) parent, and the children are re-parented to
    /// it — one transaction, fully validated.
    /// </summary>
    public Task<ConfigResult> InsertParentAsync(TargetDto newTarget, IReadOnlyList<Guid> childIds)
        => MutateAsync(async db =>
    {
        if (childIds.Count == 0)
            return ConfigResult.Fail(ConfigErrorKind.Validation, "At least one child is required.");

        var children = await db.Targets.Where(t => childIds.Contains(t.Id)).ToListAsync();
        if (children.Count != childIds.Count)
            return ConfigResult.Fail(ConfigErrorKind.Validation, "One or more children do not exist.");
        var parents = children.Select(c => c.ParentId).Distinct().ToList();
        if (parents.Count != 1)
            return ConfigResult.Fail(ConfigErrorKind.Validation,
                "All children must currently share the same parent.");

        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.Name == newTarget.Profile);
        if (profile is null)
            return ConfigResult.Fail(ConfigErrorKind.Validation, $"Unknown profile '{newTarget.Profile}'.");

        var row = new TargetRow { Id = Guid.NewGuid() }.CopyFrom(newTarget, profile.Id, parents[0]);
        db.Targets.Add(row);
        foreach (var child in children)
            child.ParentId = row.Id;
        return ConfigResult.Ok(row.Id);
    });

    public Task<ConfigResult> DeleteTargetAsync(Guid id) => MutateAsync(async db =>
    {
        var row = await db.Targets.FindAsync(id);
        if (row is null)
            return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No target with id {id}.");

        var conflicts = new List<string>();
        conflicts.AddRange(await db.Targets.Where(t => t.ParentId == id)
            .Select(t => $"child target '{t.Name}'").ToListAsync());
        conflicts.AddRange(await db.Pools.Where(p => p.ParentId == id)
            .Select(p => $"pool parent of '{p.Name}'").ToListAsync());
        conflicts.AddRange(await db.PoolMembers.Where(m => m.TargetId == id)
            .Join(db.Pools, m => m.PoolId, p => p.Id, (m, p) => $"member of pool '{p.Name}'")
            .ToListAsync());
        if (conflicts.Count > 0)
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                $"Target '{row.Name}' is referenced: {string.Join("; ", conflicts)}.");

        db.Targets.Remove(row);
        return ConfigResult.Ok(id);
    });

    public Task<ConfigResult> UpsertPoolAsync(PoolDto dto) => MutateAsync(async db =>
    {
        var memberIds = new List<Guid>();
        foreach (var name in dto.Members)
        {
            var member = await db.Targets.SingleOrDefaultAsync(t => t.Name == name);
            if (member is null)
                return ConfigResult.Fail(ConfigErrorKind.Validation, $"Unknown pool member '{name}'.");
            memberIds.Add(member.Id);
        }
        Guid? parentId = null;
        if (dto.Parent is { Length: > 0 } parentName)
        {
            var parent = await db.Targets.SingleOrDefaultAsync(t => t.Name == parentName);
            if (parent is null)
                return ConfigResult.Fail(ConfigErrorKind.Validation, $"Unknown parent target '{parentName}'.");
            parentId = parent.Id;
        }

        PoolRow row;
        if (dto.Id is { } id)
        {
            var existing = await db.Pools.Include(p => p.Members).SingleOrDefaultAsync(p => p.Id == id);
            if (existing is null)
                return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No pool with id {id}.");
            db.PoolMembers.RemoveRange(existing.Members);
            existing.Members.Clear();
            row = existing;
        }
        else
        {
            row = new PoolRow { Id = Guid.NewGuid() };
            db.Pools.Add(row);
        }
        row.Name = dto.Name;
        row.ParentId = parentId;
        row.Members.AddRange(memberIds.Select((tid, i) => new PoolMemberRow
        {
            PoolId = row.Id, TargetId = tid, MemberIndex = i,
        }));
        return ConfigResult.Ok(row.Id);
    });

    public Task<ConfigResult> DeletePoolAsync(Guid id) => MutateAsync(async db =>
    {
        var row = await db.Pools.FindAsync(id);
        if (row is null)
            return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No pool with id {id}.");
        db.Pools.Remove(row);
        return ConfigResult.Ok(id);
    });

    /// <summary>Validate-then-replace the whole configuration atomically.</summary>
    public async Task<ConfigResult> ImportAsync(ConfigDocument doc)
    {
        // Validate the incoming document before touching the database.
        var validation = new WatcherOptionsValidator().Validate(null, doc.ToOptions());
        if (validation.Failed)
            return ConfigResult.Fail(ConfigErrorKind.Validation, validation.Failures!.ToArray());

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await ConfigStore.WriteDocumentAsync(db, doc);
        await tx.CommitAsync();
        _log.LogInformation("Imported configuration: {Targets} targets, {Pools} pools",
            doc.Targets.Count, doc.Pools.Count);
        ConfigChanged?.Invoke();
        return ConfigResult.Ok();
    }

    /// <summary>
    /// Reads the whole stored document, lets <paramref name="edit"/> change it
    /// in memory, then validates and writes it back: one transaction, one
    /// validation, one ConfigChanged. Ids in the document are preserved, so
    /// rows the edit did not touch keep their identity. A failed edit or a
    /// failed validation commits nothing. Cannot change an existing target's
    /// address: that goes through <see cref="UpsertTargetAsync"/> with an
    /// explicit addressChange, which writes the HistoryReset/AddressChanged marker.
    /// </summary>
    public async Task<ConfigResult> EditDocumentAsync(Func<ConfigDocument, ConfigResult> edit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var doc = await store.ExportAsync(db);
        var addressesBefore = doc.Targets
            .Where(t => t.Id is not null)
            .ToDictionary(t => t.Id!.Value, t => ConfigMapping.NormalizeAddress(t.Address));

        var result = edit(doc);
        if (!result.Success)
            return result;

        foreach (var target in doc.Targets)
        {
            if (target.Id is not { } id || !addressesBefore.TryGetValue(id, out var oldAddress))
                continue;
            if (!string.Equals(oldAddress, ConfigMapping.NormalizeAddress(target.Address), StringComparison.OrdinalIgnoreCase))
                return ConfigResult.Fail(ConfigErrorKind.Validation,
                    "Address edits go through UpsertTargetAsync with addressChange; EditDocumentAsync cannot change an existing target's address.");
        }

        var validation = new WatcherOptionsValidator().Validate(null, doc.ToOptions());
        if (validation.Failed)
            return ConfigResult.Fail(ConfigErrorKind.Validation, validation.Failures!.ToArray());

        try
        {
            await ConfigStore.WriteDocumentAsync(db, doc);
        }
        catch (DbUpdateException ex) when (SqliteErrors.IsUniqueViolation(ex))
        {
            _log.LogDebug(ex, "Config edit rejected by a uniqueness constraint");
            return ConfigResult.Fail(ConfigErrorKind.Validation,
                "Change violates a uniqueness constraint (duplicate name or address).");
        }

        await tx.CommitAsync();
        ConfigChanged?.Invoke();
        return result;
    }

    /// <summary>Apply a mutation, validate the whole resulting config, commit or roll back.</summary>
    private async Task<ConfigResult> MutateAsync(Func<ConWatchDbContext, Task<ConfigResult>> mutation)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        ConfigResult result;
        try
        {
            result = await mutation(db);
            if (!result.Success)
                return result;
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (SqliteErrors.IsUniqueViolation(ex))
        {
            _log.LogDebug(ex, "Config mutation rejected by a uniqueness constraint");
            return ConfigResult.Fail(ConfigErrorKind.Validation,
                "Change violates a uniqueness constraint (duplicate name or address).");
        }

        var options = await store.LoadWatcherOptionsAsync(db);
        var validation = new WatcherOptionsValidator().Validate(null, options);
        if (validation.Failed)
            return ConfigResult.Fail(ConfigErrorKind.Validation, validation.Failures!.ToArray());

        await tx.CommitAsync();
        ConfigChanged?.Invoke();
        return result;
    }
}
