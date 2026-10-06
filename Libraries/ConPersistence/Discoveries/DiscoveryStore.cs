using System.Net;
using ConCore.Discovery;
using ConPersistence.Config;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Discoveries;

/// <summary>One candidate as posted by a search or a scout.</summary>
public sealed record DiscoveryInput(string Address, string? Hostname, DiscoveryKind Kind,
    DiscoveryMethod Method, string Origin, string? Evidence);

public sealed record UpsertResult(bool Success, IReadOnlyList<string> Errors, int Created, int Updated);

/// <summary>The wire/UI shape. <see cref="AdoptedTargetName"/> is null when the
/// adopted target no longer exists ("(target removed)").</summary>
public sealed record DiscoveryDto(Guid Id, string Address, string? Hostname, DiscoveryKind Kind,
    DiscoveryMethod Method, string Origin, string? Evidence, DateTime FirstSeenUtc, DateTime LastSeenUtc,
    DiscoveryStatus Status, Guid? AdoptedTargetId, string? AdoptedTargetName);

public static class DiscoveryStatuses
{
    /// <summary>"new" (also null/empty), "dismissed", "adopted" or "all" (status = null), case-insensitive.</summary>
    public static bool TryParseFilter(string? text, out DiscoveryStatus? status)
    {
        status = DiscoveryStatus.New;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "new": return true;
            case "dismissed": status = DiscoveryStatus.Dismissed; return true;
            case "adopted": status = DiscoveryStatus.Adopted; return true;
            case "all": status = null; return true;
            default: status = null; return false;
        }
    }
}

/// <summary>
/// The Discoveries inbox. Upserts on (Address, Kind): a rescan refreshes LastSeen,
/// evidence, method and origin but never changes Status, so a dismissed address
/// stays dismissed. Adoption itself happens in ConfigService, inside the
/// target-create transaction.
/// </summary>
public sealed class DiscoveryStore(IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider? timeProvider = null)
{
    public const int MaxBatch = DiscoveryLimits.MaxBatch;
    private const int MaxHostname = 255;
    private const int MaxOrigin = 100;
    private const int MaxEvidence = 1000;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Every problem in the batch, numbered from 1; empty when the batch is acceptable.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<DiscoveryInput> inputs)
    {
        var errors = new List<string>();
        if (inputs.Count == 0)
            errors.Add("At least one discovery is required.");
        if (inputs.Count > MaxBatch)
            errors.Add($"At most {MaxBatch} discoveries per request.");
        for (var i = 0; i < inputs.Count && i < MaxBatch; i++)
        {
            var item = inputs[i];
            var n = i + 1;
            if (!TryNormalizeAddress(item?.Address, out _))
                errors.Add($"Item {n}: '{item?.Address}' is not a usable IP address.");
            if (item is null)
                continue;
            if (!Enum.IsDefined(item.Kind))
                errors.Add($"Item {n}: unknown kind.");
            if (!Enum.IsDefined(item.Method))
                errors.Add($"Item {n}: unknown method.");
            if (string.IsNullOrWhiteSpace(item.Origin) || item.Origin.Trim().Length > MaxOrigin)
                errors.Add($"Item {n}: origin is required and at most {MaxOrigin} characters.");
            if (item.Hostname is { } h && h.Trim().Length > MaxHostname)
                errors.Add($"Item {n}: hostname is longer than {MaxHostname} characters.");
            if (item.Evidence is { } e && e.Trim().Length > MaxEvidence)
                errors.Add($"Item {n}: evidence is longer than {MaxEvidence} characters.");
        }
        return errors;
    }

    private static bool TryNormalizeAddress(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        // IPAddress.TryParse accepts legacy short forms ("1" is 0.0.0.1, "1.2.3" is 1.2.0.3):
        // demand a full dotted quad or an IPv6 literal.
        if (!trimmed.Contains(':') && trimmed.Count(c => c == '.') != 3)
            return false;
        // A scope id ("fe80::1%eth0") names a local interface; it means nothing to another node.
        // (ScopeId throws on an IPv4 address, so only ask IPv6.)
        if (trimmed.Contains('%') || !IPAddress.TryParse(trimmed, out var ip) ||
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0))
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.Broadcast) || ip.Equals(IPAddress.None))
            return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var first = ip.GetAddressBytes()[0];
            if (first == 0 || first >= 224)           // 0.0.0.0/8 "this network"; multicast and reserved
                return false;
        }
        else if (ip.IsIPv6Multicast)
        {
            return false;
        }
        normalized = ip.ToString();
        return true;
    }

    private static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public async Task<UpsertResult> UpsertAsync(IReadOnlyList<DiscoveryInput> inputs)
    {
        var errors = Validate(inputs);
        if (errors.Count > 0)
            return new UpsertResult(false, errors, 0, 0);

        var now = _time.GetUtcNow().UtcDateTime;
        // Last one wins when a batch names the same (address, kind) twice.
        var batch = new Dictionary<(string Address, DiscoveryKind Kind), DiscoveryInput>();
        foreach (var item in inputs)
        {
            TryNormalizeAddress(item.Address, out var address);
            batch[(address, item.Kind)] = item with
            {
                Address = address, Hostname = Trim(item.Hostname), Origin = item.Origin.Trim(),
                Evidence = Trim(item.Evidence),
            };
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var addresses = batch.Keys.Select(k => k.Address).Distinct().ToList();
        var existing = (await db.Discoveries.Where(d => addresses.Contains(d.Address)).ToListAsync())
            .ToDictionary(d => (d.Address, d.Kind));

        int created = 0, updated = 0;
        foreach (var (key, item) in batch)
        {
            if (existing.TryGetValue(key, out var row))
            {
                row.Method = item.Method;
                row.Origin = item.Origin;
                row.Evidence = item.Evidence;
                row.Hostname = item.Hostname ?? row.Hostname;
                row.LastSeenUtc = now;
                updated++;
            }
            else
            {
                db.Discoveries.Add(new DiscoveryRow
                {
                    Id = Guid.NewGuid(), Address = item.Address, Hostname = item.Hostname,
                    Kind = item.Kind, Method = item.Method, Origin = item.Origin, Evidence = item.Evidence,
                    FirstSeenUtc = now, LastSeenUtc = now, Status = DiscoveryStatus.New,
                });
                created++;
            }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new UpsertResult(true, [], created, updated);
    }

    /// <param name="status">Null means every status.</param>
    public async Task<List<DiscoveryDto>> ListAsync(DiscoveryStatus? status)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        IQueryable<DiscoveryRow> query = db.Discoveries.AsNoTracking();
        if (status is { } s)
            query = query.Where(d => d.Status == s);
        var rows = await query.OrderByDescending(d => d.LastSeenUtc).ThenBy(d => d.Address).ToListAsync();

        var adoptedIds = rows.Where(r => r.AdoptedTargetId != null).Select(r => r.AdoptedTargetId!.Value).ToList();
        var names = adoptedIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Targets.AsNoTracking().Where(t => adoptedIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name);
        return rows.Select(r => new DiscoveryDto(r.Id, r.Address, r.Hostname, r.Kind, r.Method, r.Origin,
            r.Evidence, r.FirstSeenUtc, r.LastSeenUtc, r.Status, r.AdoptedTargetId,
            r.AdoptedTargetId is { } id && names.TryGetValue(id, out var name) ? name : null)).ToList();
    }

    public async Task<ConfigResult> DismissAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.Discoveries.FindAsync(id);
        if (row is null)
            return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No discovery with id {id}.");
        if (await IsLiveAdoptionAsync(db, row))
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                "This discovery was adopted into a target; delete the target first.");
        row.Status = DiscoveryStatus.Dismissed;
        row.AdoptedTargetId = null;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ConfigResult.Ok(id);
    }

    public async Task<ConfigResult> RestoreAsync(Guid id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.Discoveries.FindAsync(id);
        if (row is null)
            return ConfigResult.Fail(ConfigErrorKind.NotFound, $"No discovery with id {id}.");
        if (await IsLiveAdoptionAsync(db, row))
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                "This discovery was adopted into a target; delete the target first.");
        row.Status = DiscoveryStatus.New;
        row.AdoptedTargetId = null;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return ConfigResult.Ok(id);
    }

    /// <summary>Puts every dismissed discovery back to New; returns how many.</summary>
    public async Task<int> RestoreAllDismissedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Discoveries.Where(d => d.Status == DiscoveryStatus.Dismissed)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DiscoveryStatus.New));
    }

    private static async Task<bool> IsLiveAdoptionAsync(ConWatchDbContext db, DiscoveryRow row) =>
        row.Status == DiscoveryStatus.Adopted && row.AdoptedTargetId is { } target &&
        await db.Targets.AnyAsync(t => t.Id == target);
}
