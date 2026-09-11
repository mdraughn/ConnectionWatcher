using ConPersistence;
using ConPersistence.Dto;

namespace ConWatch;

public record SetupTarget(string Name, string Address);
public record SetupApplyRequest(SetupTarget Router, SetupTarget? OuterIp, List<string> AnchorAddresses,
    string? IspName = null);

/// <summary>
/// Applies the quick-setup wizard's choices additively: existing names and
/// addresses are left alone; missing pieces are created and chained
/// Router → OuterIp? → anchors → pool "Internet" → verdict settings.
/// </summary>
public class SetupService(ConfigService config, ConfigStore store, SettingsStore settings,
    ILogger<SetupService> log)
{
    public const string PoolName = "Internet";

    /// <summary>The wizard's anchor catalog: anycast resolvers built for public traffic.</summary>
    public static readonly IReadOnlyList<SetupTarget> AnchorCatalog =
    [
        new("Google DNS", "8.8.8.8"),
        new("Cloudflare DNS", "1.1.1.1"),
        new("Quad9 DNS", "9.9.9.9"),
        new("OpenDNS", "208.67.222.222"),
    ];

    public async Task<ConfigResult> ApplyAsync(SetupApplyRequest request)
    {
        var existing = await store.ExportAsync();
        string? ByAddress(string address) => existing.Targets
            .FirstOrDefault(t => t.Address.Equals(address, StringComparison.OrdinalIgnoreCase))?.Name;
        bool NameTaken(string name) => existing.Targets
            .Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // Router: reuse any target already at that address.
        var routerName = ByAddress(request.Router.Address);
        if (routerName is null)
        {
            routerName = Unique(request.Router.Name, NameTaken);
            var result = await config.UpsertTargetAsync(new TargetDto
            {
                Name = routerName,
                Address = request.Router.Address,
                Profile = "Internal",
            });
            if (!result.Success)
                return result;
        }

        // Optional WAN address, chained under the router.
        var pathLeaf = routerName;
        if (request.OuterIp is { } outer)
        {
            var outerName = ByAddress(outer.Address);
            if (outerName is null)
            {
                outerName = Unique(outer.Name, NameTaken);
                var result = await config.UpsertTargetAsync(new TargetDto
                {
                    Name = outerName,
                    Address = outer.Address,
                    Parent = routerName,
                    Profile = "Internal",
                    // The WAN hop is where the ISP's service begins.
                    Operator = request.IspName,
                });
                if (!result.Success)
                    return result;
            }
            pathLeaf = outerName;
        }

        // Anchors under the path leaf.
        var anchorNames = new List<string>();
        foreach (var address in request.AnchorAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var known = ByAddress(address);
            if (known is not null)
            {
                anchorNames.Add(known);
                continue;
            }
            var catalogName = AnchorCatalog.FirstOrDefault(a => a.Address == address)?.Name ?? address;
            var name = Unique(catalogName, NameTaken);
            var result = await config.UpsertTargetAsync(new TargetDto
            {
                Name = name, Address = address, Parent = pathLeaf, Profile = "External",
            });
            if (!result.Success)
                return result;
            anchorNames.Add(name);
        }

        // Pool: create, or union in any new members.
        var pool = existing.Pools.FirstOrDefault(p =>
            p.Name.Equals(PoolName, StringComparison.OrdinalIgnoreCase));
        if (anchorNames.Count > 0)
        {
            var members = pool is null
                ? anchorNames
                : pool.Members.Concat(anchorNames.Where(n => !pool.Members.Contains(n))).ToList();
            var poolResult = await config.UpsertPoolAsync(new PoolDto
            {
                Id = pool?.Id, Name = pool?.Name ?? PoolName, Members = members,
                Parent = pool?.Parent ?? pathLeaf,
            });
            if (!poolResult.Success)
                return poolResult;
        }

        // Verdict settings: only fill what is unset.
        if (await settings.GetAsync(SettingKeys.AnchorPool) is not { Length: > 0 })
            await settings.SetManyAsync(new Dictionary<string, string?>
            {
                [SettingKeys.AnchorPool] = pool?.Name ?? PoolName,
            });

        log.LogInformation("Setup applied: router {Router}, {AnchorCount} anchors",
            routerName, anchorNames.Count);
        return ConfigResult.Ok();
    }

    private static string Unique(string name, Func<string, bool> taken)
    {
        if (!taken(name))
            return name;
        for (int i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (!taken(candidate))
                return candidate;
        }
    }
}
