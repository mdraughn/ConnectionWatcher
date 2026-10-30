using ConCore.Targets;
using ConPersistence.Entities;
using ConPersistence.Queries;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Config;

/// <summary>One structured configuration warning.</summary>
public record HealthWarning(string Code, string Message)
{
    /// <summary>The page where this warning is fixed, so a banner can send the person
    /// there rather than to a tab that cannot act on it.</summary>
    public string Page => Code switch
    {
        "dns-not-watched" or "no-upstream-resolver" or "dns-over-https" => "/Config/Dns",
        "no-anchor-pool" or "anchor-pool-missing" or "anchors-below-threshold"
            or "anchors-below-recommended" or "pool-parent-drift" => "/Config/Pools",
        "clone-source-active" => "/Settings",
        _ => "/Config/Targets", // anchors-all-isp (operator tags), tcp-refused (probe), anything new
    };

    /// <summary>The page's name as the UI labels it ("DNS", "Pools", "Targets", "Settings").</summary>
    public string PageName => Page[(Page.LastIndexOf('/') + 1)..] switch
    {
        "Dns" => "DNS",
        var name => name,
    };
}

/// <summary>
/// Evaluates whether the current configuration can actually deliver what it
/// promises — chiefly whether the verdict layer has enough anchors to tell
/// internet weather from a single site's incident.
/// </summary>
public class ConfigHealth(IDbContextFactory<ConWatchDbContext> dbFactory, SettingsStore settings, ConCore.Dns.IDohDetector? doh = null)
{
    private const int RecommendedAnchors = 3;

    public async Task<IReadOnlyList<HealthWarning>> EvaluateAsync()
    {
        var warnings = new List<HealthWarning>();
        await AddDnsWarningsAsync(warnings);
        var anchorPool = await settings.GetAsync(SettingKeys.AnchorPool);
        if (anchorPool is not { Length: > 0 })
        {
            warnings.Add(new HealthWarning("no-anchor-pool",
                "No anchor pool is configured — internet weather detection is off."));
            return warnings;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var pool = await TopologyQueries.AnchorPoolAsync(db, anchorPool);
        if (pool is null)
        {
            warnings.Add(new HealthWarning("anchor-pool-missing",
                $"The verdict anchor pool is set to '{anchorPool}', but no such pool exists."));
            return warnings;
        }

        // Every stored target, enabled or not: the engine walks through disabled hops,
        // but only enabled targets are monitored, so only they count as anchors or tagged hops.
        var byId = await db.Targets.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var enabledAnchors = pool.Members
            .Select(m => byId.GetValueOrDefault(m.TargetId))
            .OfType<TargetRow>().Where(t => t.Enabled).ToList();

        var threshold = await settings.GetIntAsync(SettingKeys.WeatherThreshold, SettingsDefaults.WeatherThreshold);
        var anchors = enabledAnchors.Count;
        if (anchors < threshold)
            warnings.Add(new HealthWarning("anchors-below-threshold",
                $"Pool '{anchorPool}' has {anchors} anchor(s) but the weather threshold is {threshold} — " +
                "internet weather detection can never trigger."));
        if (anchors < RecommendedAnchors)
            warnings.Add(new HealthWarning("anchors-below-recommended",
                $"Pool '{anchorPool}' has {anchors} anchor(s); {RecommendedAnchors} or more give a more reliable verdict."));

        if (AllAnchorsIspOwned(pool, enabledAnchors, byId) is { } unprovable)
            warnings.Add(unprovable);

        // Pool-parent drift: re-parenting the members does not move the pool,
        // and a pool parented above (or without) the members' real parent
        // silently truncates the ISP report's trunk walk. The verdict layer
        // is immune (it walks anchor chains), which makes this easy to miss.
        if (PoolParentDrift(pool, byId) is { } drift)
            warnings.Add(drift);
        return warnings;
    }

    /// <summary>Name-resolution watch (spec 2026-10-10 §4.5). Runs first so a node
    /// without an anchor pool still gets DNS health.</summary>
    private async Task AddDnsWarningsAsync(List<HealthWarning> warnings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var dnsEnabled = await settings.GetBoolAsync(SettingKeys.DnsEnabled);
        var names = ConCore.Dns.DnsLookupName.FromJson(await settings.GetAsync(SettingKeys.DnsLookupNames));
        var addresses = await db.Targets.Where(t => t.Enabled && t.Scope != ConCore.Targets.TargetScope.Resolver)
            .Select(t => t.Address).ToListAsync();
        var hasHostnameTarget = addresses.Any(a => !System.Net.IPAddress.TryParse(a, out _));
        var systemResolvers = await db.Targets.CountAsync(t => t.Scope == ConCore.Targets.TargetScope.Resolver && t.SystemManaged && t.Enabled);
        if (!dnsEnabled || (names.Count == 0 && !hasHostnameTarget) || systemResolvers == 0)
            warnings.Add(new HealthWarning("dns-not-watched",
                !dnsEnabled ? "Name resolution isn't being watched: the DNS watch is turned off (Dns.Enabled)."
                : systemResolvers == 0 ? "Name resolution isn't being watched: no resolver from this machine's network settings is enabled (none was found, or each is disabled)."
                : "Name resolution isn't being watched: the lookup-name list is empty and no hostname target is configured."));
        else if (!await db.Targets.AnyAsync(t => t.Scope == ConCore.Targets.TargetScope.Resolver && !t.SystemManaged && t.Enabled))
            warnings.Add(new HealthWarning("no-upstream-resolver",
                "Name a public resolver (1.1.1.1, 8.8.8.8, …) on the DNS tab so ConnectionWatcher can tell your resolver from everyone's."));

        if (doh is not null && dnsEnabled)
        {
            var enabledSystem = await db.Targets.Where(t => t.Scope == ConCore.Targets.TargetScope.Resolver && t.SystemManaged && t.Enabled)
                .Select(t => t.Address).ToListAsync();
            foreach (var address in doh.AddressesUsingDoh(enabledSystem))
                warnings.Add(new HealthWarning("dns-over-https",
                    $"Windows resolves through DNS-over-HTTPS for {address}; the check measures port 53, so it may not reflect what Windows actually uses."));
        }
    }

    /// <summary>Every counted anchor carries the ISP's operator tag: the verdict then
    /// falls back to the ISP's own servers and cannot prove the internet
    /// beyond the ISP is up (spec 2026-10-09 §4.1, the UnprovableUp note). Judged with
    /// the engine's own rule over enabled targets.</summary>
    private static HealthWarning? AllAnchorsIspOwned(Entities.PoolRow pool,
        IReadOnlyList<TargetRow> enabledAnchors, Dictionary<Guid, TargetRow> byId)
    {
        TargetRow? ParentOf(TargetRow t) => t.ParentId is { } pid ? byId.GetValueOrDefault(pid) : null;

        var trunk = TreeWalk.TrunkOf(enabledAnchors, ParentOf).Where(t => t.Enabled);
        IspRule.CountedAnchors(enabledAnchors, trunk, t => t.Operator, out var usingIspFallback);
        if (!usingIspFallback)
            return null;

        return new HealthWarning("anchors-all-isp",
            $"Every anchor in pool '{pool.Name}' is tagged as your ISP's, so ConnectionWatcher can only " +
            "prove your ISP answers, not the rest of the internet. Tag your ISP on a path hop " +
            "(or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up.");
    }

    private static HealthWarning? PoolParentDrift(Entities.PoolRow pool, Dictionary<Guid, TargetRow> targets)
    {
        // Each member's ancestor chain, nearest first.
        var chains = new List<List<Guid>>();
        foreach (var member in pool.Members)
        {
            if (!targets.TryGetValue(member.TargetId, out var target))
                continue;
            var chain = TreeWalk.ChainToRoot(target,
                    t => t.ParentId is { } pid && targets.TryGetValue(pid, out var parent) ? parent : null)
                .Skip(1).Select(t => t.Id).ToList();
            chains.Add(chain);
        }
        if (chains.Count == 0)
            return null;

        // Deepest ancestor shared by every member — where the pool belongs.
        var expected = chains[0].FirstOrDefault(id => chains.All(c => c.Contains(id)));
        if (expected == Guid.Empty || pool.ParentId == expected)
            return null;

        var expectedName = targets[expected].Name;
        var current = pool.ParentId is { } cur && targets.TryGetValue(cur, out var parent)
            ? $"parented to '{parent.Name}'" : "not parented to anything";
        return new HealthWarning("pool-parent-drift",
            $"Pool '{pool.Name}' is {current}, but its members sit behind '{expectedName}' — " +
            $"reports that walk the path from the pool's parent will miss hops. " +
            $"Re-parent the pool to '{expectedName}'.");
    }
}
