using ConCore.Targets;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;

namespace ConWatch;

public record SetupTarget(string Name, string Address, string? Operator = null, string? LookupName = null);
public record SetupApplyRequest(SetupTarget Router, SetupTarget? OuterIp, List<string> AnchorAddresses,
    string? IspName = null, string? UpstreamResolverAddress = null);

/// <summary>
/// Applies the quick-setup wizard's choices additively: existing names and
/// addresses are left alone; missing pieces are created and chained
/// Router → OuterIp? → anchors → pool "Internet" → verdict settings.
/// </summary>
public class SetupService(ConfigService config, SettingsStore settings, ILogger<SetupService> log)
{
    public const string PoolName = "Internet";

    /// <summary>The wizard's anchor catalog: anycast resolvers built for public
    /// traffic, one per operator. The operator tag is what the verdict counts
    /// as independent evidence (see VerdictCoordinator).</summary>
    public static readonly IReadOnlyList<SetupTarget> AnchorCatalog =
    [
        new("Google DNS", "8.8.8.8", "Google", "dns.google"),
        new("Cloudflare DNS", "1.1.1.1", "Cloudflare", "one.one.one.one"),
        new("Quad9 DNS", "9.9.9.9", "Quad9", "dns.quad9.net"),
        new("OpenDNS", "208.67.222.222", "OpenDNS", "dns.opendns.com"),
    ];

    public async Task<ConfigResult> ApplyAsync(SetupApplyRequest request)
    {
        var routerName = "";
        var poolName = PoolName;
        var anchorNames = new List<string>();

        // One read-modify-write of the whole document: the wizard is all or
        // nothing, and the engine rebuilds once.
        var result = await config.EditDocumentAsync(doc =>
        {
            string? ByAddress(string address) => doc.Targets
                .FirstOrDefault(t => t.Scope == TargetScope.Verdict
                    && t.Address.Equals(address, StringComparison.OrdinalIgnoreCase))?.Name;
            bool NameTaken(string name) => doc.Targets
                .Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string Add(TargetDto target)
            {
                doc.Targets.Add(target);
                return target.Name;
            }

            // Router: reuse any Verdict-scope target already at that address (a resolver or
            // Local target there is a different thing, and cannot parent children).
            // A router the wizard creates is critical (spec §4.7); a reused target is left as it is (D6).
            routerName = ByAddress(request.Router.Address) ?? Add(new TargetDto
            {
                Name = Unique(request.Router.Name, NameTaken),
                Address = request.Router.Address,
                Profile = "Internal",
                Critical = true,
            });

            // Optional WAN address, chained under the router.
            var pathLeaf = routerName;
            if (request.OuterIp is { } outer)
            {
                pathLeaf = ByAddress(outer.Address) ?? Add(new TargetDto
                {
                    Name = Unique(outer.Name, NameTaken),
                    Address = outer.Address,
                    Parent = routerName,
                    Profile = "Path",
                    // The WAN hop is where the ISP's service begins.
                    Operator = request.IspName,
                });
            }

            // Anchors under the path leaf.
            foreach (var address in request.AnchorAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var catalogEntry = AnchorCatalog.FirstOrDefault(a => a.Address == address);
                anchorNames.Add(ByAddress(address) ?? Add(new TargetDto
                {
                    Name = Unique(catalogEntry?.Name ?? address, NameTaken),
                    Address = address, Parent = pathLeaf, Profile = "Public",
                    Operator = catalogEntry?.Operator,
                }));
            }

            // The public resolver asked directly (spec 2026-10-10 §5.3): the one named,
            // else the first anchor chosen. Reused when a Resolver target is already there.
            var upstream = string.IsNullOrWhiteSpace(request.UpstreamResolverAddress)
                ? request.AnchorAddresses.FirstOrDefault()
                : request.UpstreamResolverAddress.Trim();
            if (upstream is { Length: > 0 } && !doc.Targets.Any(t => t.Scope == TargetScope.Resolver
                    && t.Address.Equals(upstream, StringComparison.OrdinalIgnoreCase)))
            {
                Add(new TargetDto
                {
                    Name = Unique($"{upstream} directly", NameTaken), Address = upstream,
                    Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver,
                });
            }

            // Pool: create, or union in any new members.
            var poolIndex = doc.Pools.FindIndex(p =>
                p.Name.Equals(PoolName, StringComparison.OrdinalIgnoreCase));
            var pool = poolIndex >= 0 ? doc.Pools[poolIndex] : null;
            poolName = pool?.Name ?? PoolName;
            if (anchorNames.Count > 0)
            {
                var merged = new PoolDto
                {
                    Id = pool?.Id,
                    Name = poolName,
                    Members = pool is null
                        ? [.. anchorNames]
                        : [.. pool.Members, .. anchorNames.Where(n => !pool.Members.Contains(n))],
                    Parent = pool?.Parent ?? pathLeaf,
                };
                if (poolIndex >= 0)
                    doc.Pools[poolIndex] = merged;
                else
                    doc.Pools.Add(merged);
            }
            return ConfigResult.Ok();
        });
        if (!result.Success)
            return result;

        // Verdict settings: only fill what is unset. This write happens after the
        // config transaction above has already committed; if it fails here, the
        // config is committed without AnchorPool set. Re-running the wizard repairs
        // it, since this apply is additive (only fills what is unset).
        if (await settings.GetAsync(SettingKeys.AnchorPool) is not { Length: > 0 })
            await settings.SetManyAsync(new Dictionary<string, string?>
            {
                [SettingKeys.AnchorPool] = poolName,
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
