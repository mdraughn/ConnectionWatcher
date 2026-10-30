using ConCore.Discovery;
using ConEngine.Configuration;
using ConPersistence.Discoveries;
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

    public const int MaxDescriptionLength = 500;

    private static string? DescriptionError(ProfileDto dto) =>
        ConfigMapping.NormalizeDescription(dto.Description) is { Length: > MaxDescriptionLength }
            ? $"Profile '{dto.Name}': description is longer than {MaxDescriptionLength} characters."
            : null;

    /// <summary>Raised after any committed config change; the host debounces rebuilds.</summary>
    public event Action? ConfigChanged;

    public Task<ConfigResult> UpsertProfileAsync(ProfileDto dto) => MutateAsync(async db =>
    {
        if (DescriptionError(dto) is { } descriptionError)
            return ConfigResult.Fail(ConfigErrorKind.Validation, descriptionError);
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

    public Task<ConfigResult> UpsertTargetAsync(TargetDto dto, string? addressChange = null,
        Guid? adoptDiscoveryId = null) => MutateAsync(async db =>
    {
        // Adopting a discovery creates a NEW target and marks the discovery in the same
        // transaction: if anything below (or the post-save validation) fails, neither happens.
        DiscoveryRow? discovery = null;
        if (adoptDiscoveryId is { } discoveryId)
        {
            if (dto.Id is not null)
                return ConfigResult.Fail(ConfigErrorKind.Validation,
                    "A discovery can only be adopted into a new target.");
            discovery = await db.Discoveries.FindAsync(discoveryId);
            if (discovery is null)
                return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No discovery with id {discoveryId}.");
            if (await DiscoveryStore.IsLiveAdoptionAsync(db, discovery))
                return ConfigResult.Fail(ConfigErrorKind.Conflict,
                    "This discovery was already adopted into a target.");
        }

        var (profile, profileError) = await ProfileNamedAsync(db, dto.Profile);
        if (profileError is not null)
            return profileError;

        var (parentId, parentError) = await ParentIdNamedAsync(db, dto.Parent);
        if (parentError is not null)
            return parentError;

        TargetRow row;
        var wasManaged = false;
        if (dto.Id is { } id)
        {
            var existing = await db.Targets.FindAsync(id);
            if (existing is null)
                return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No target with id {id}.");
            row = existing;
            wasManaged = existing.SystemManaged;

            if (existing.SystemManaged)
            {
                // The host's OS resolver list owns this row: only name, enabled,
                // operator and critical are the admin's to change (spec 2026-10-10 §4.1).
                var frozen = new List<string>();
                if (!string.Equals(existing.Address, ConfigMapping.NormalizeAddress(dto.Address), StringComparison.OrdinalIgnoreCase))
                    frozen.Add("address");
                if (parentId is not null) frozen.Add("parent");
                if (existing.ProfileId != profile!.Id) frozen.Add("profile");
                if (!string.Equals(existing.Probe, ConfigMapping.NormalizeProbe(dto.Probe), StringComparison.OrdinalIgnoreCase))
                    frozen.Add("probe");
                if (existing.Scope != dto.Scope) frozen.Add("scope");
                if (!dto.SystemManaged) frozen.Add("systemManaged");
                if (frozen.Count > 0)
                    return ConfigResult.Fail(ConfigErrorKind.Validation,
                        $"Target '{existing.Name}' is managed by ConnectionWatcher (a system resolver); " +
                        $"{string.Join(", ", frozen)} cannot be changed. You can disable or rename it.");
            }

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
        row.CopyFrom(dto, profile!.Id, parentId);
        // The flag is the reconciler's (EditDocumentAsync), never the API's: a
        // new row is never managed, an existing row keeps what it had.
        row.SystemManaged = wasManaged;
        if (discovery is not null)
        {
            discovery.Status = DiscoveryStatus.Adopted;
            discovery.AdoptedTargetId = row.Id;
        }
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

        var (profile, profileError) = await ProfileNamedAsync(db, newTarget.Profile);
        if (profileError is not null)
            return profileError;

        var row = new TargetRow { Id = Guid.NewGuid() }.CopyFrom(newTarget, profile!.Id, parents[0]);
        row.SystemManaged = false; // a new row is never managed
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
        if (row.SystemManaged)
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                $"Target '{row.Name}' is managed by ConnectionWatcher (a system resolver); disable it instead of deleting it.");

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
        var (parentId, parentError) = await ParentIdNamedAsync(db, dto.Parent);
        if (parentError is not null)
            return parentError;

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
        // A foreign node's system resolvers never come in (spec 2026-10-10 §5.1).
        if (doc.Targets.Any(t => t.SystemManaged))
            doc = doc with { Targets = doc.Targets.Where(t => !t.SystemManaged).ToList() };

        // Validate the incoming document before touching the database.
        var descriptionErrors = doc.Profiles.Select(DescriptionError).Where(e => e is not null).ToArray();
        if (descriptionErrors.Length > 0)
            return ConfigResult.Fail(ConfigErrorKind.Validation, descriptionErrors!);
        if (Invalid(doc.ToOptions()) is { } invalid)
            return invalid;

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        if (await WriteDocumentCheckedAsync(db, doc, "Config import") is { } writeFailure)
            return writeFailure;

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

        if (Invalid(doc.ToOptions()) is { } invalid)
            return invalid;

        if (await WriteDocumentCheckedAsync(db, doc, "Config edit") is { } writeFailure)
            return writeFailure;

        await tx.CommitAsync();
        ConfigChanged?.Invoke();
        return result;
    }

    private ConfigResult UniquenessFailure(DbUpdateException ex, string what)
    {
        _log.LogDebug(ex, "{What} rejected by a uniqueness constraint", what);
        return ConfigResult.Fail(ConfigErrorKind.Validation,
            "Change violates a uniqueness constraint (duplicate name or address).");
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
            return UniquenessFailure(ex, "Config mutation");
        }

        var options = await store.LoadWatcherOptionsAsync(db);
        if (Invalid(options) is { } invalid)
            return invalid;

        await tx.CommitAsync();
        ConfigChanged?.Invoke();
        return result;
    }

    /// <summary>Null when the options pass the validator, else the Validation failure.</summary>
    private static ConfigResult? Invalid(WatcherOptions options)
    {
        var validation = new WatcherOptionsValidator().Validate(null, options);
        return validation.Failed
            ? ConfigResult.Fail(ConfigErrorKind.Validation, validation.Failures!.ToArray())
            : null;
    }

    /// <summary>
    /// Writes the document; a unique-constraint violation comes back as the
    /// uniqueness failure (null on success). The caller commits.
    /// </summary>
    private async Task<ConfigResult?> WriteDocumentCheckedAsync(ConWatchDbContext db, ConfigDocument doc, string what)
    {
        try
        {
            await ConfigStore.WriteDocumentAsync(db, doc);
            return null;
        }
        catch (DbUpdateException ex) when (SqliteErrors.IsUniqueViolation(ex))
        {
            return UniquenessFailure(ex, what);
        }
    }

    private static async Task<(ProfileRow? Row, ConfigResult? Error)> ProfileNamedAsync(ConWatchDbContext db, string name)
    {
        var profile = await db.Profiles.SingleOrDefaultAsync(p => p.Name == name);
        return profile is null
            ? (null, ConfigResult.Fail(ConfigErrorKind.Validation, $"Unknown profile '{name}'."))
            : (profile, null);
    }

    private static async Task<(Guid? Id, ConfigResult? Error)> ParentIdNamedAsync(ConWatchDbContext db, string? name)
    {
        if (string.IsNullOrEmpty(name))
            return (null, null);
        var parent = await db.Targets.SingleOrDefaultAsync(t => t.Name == name);
        return parent is null
            ? (null, ConfigResult.Fail(ConfigErrorKind.Validation, $"Unknown parent target '{name}'."))
            : (parent.Id, null);
    }
}
