using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Settings;

namespace ConWatch;

/// <summary>
/// Trunk and anchor roles from the STORED configuration — the graph the
/// engine is built from — for the dashboard, History and the Targets
/// pipeline, whether or not an engine is running. The one rule (owner
/// decision 2026-09-18): the trunk is the UNION of every anchor's ancestor
/// chain.
/// </summary>
public static class TopologyNames
{
    /// <param name="TrunkNames">Every ancestor of every anchor, first-seen order walking each anchor upward.</param>
    /// <param name="AnchorNames">The anchor pool's members that exist, in pool order.</param>
    /// <param name="TrunkChains">One root-first chain per distinct anchor parent, in first-seen order; their union is <paramref name="TrunkNames"/>.</param>
    /// <param name="HasAnchorPool">The AnchorPool setting names an existing pool (the engine then runs a verdict).</param>
    /// <param name="WeatherThreshold">The Verdict.WeatherThreshold setting.</param>
    public sealed record Names(List<string> TrunkNames, List<string> AnchorNames,
        List<List<string>> TrunkChains, bool HasAnchorPool, int WeatherThreshold, ConfigDocument Config);

    public static async Task<Names> FromConfigAsync(ConfigStore configStore, SettingsStore settingsStore) =>
        Compute(await configStore.ExportAsync(),
            await settingsStore.GetAsync(SettingKeys.AnchorPool),
            await settingsStore.GetIntAsync(SettingKeys.WeatherThreshold, SettingsDefaults.WeatherThreshold));

    public static Names Compute(ConfigDocument config, string? anchorPoolName, int weatherThreshold)
    {
        var pool = anchorPoolName is { Length: > 0 }
            ? config.Pools.FirstOrDefault(p =>
                p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (pool is null)
            return new Names([], [], [], HasAnchorPool: false, weatherThreshold, config);

        var byName = config.Targets
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        TargetDto? ParentOf(TargetDto t) =>
            t.Parent is { Length: > 0 } parent && byName.TryGetValue(parent, out var p) ? p : null;

        var members = pool.Members.Where(byName.ContainsKey).Select(m => byName[m]).ToList();
        var trunk = TreeWalk.AncestorsOf(members, ParentOf).Select(t => t.Name).ToList();
        var chains = members
            .Select(ParentOf).OfType<TargetDto>()
            .DistinctBy(leaf => leaf.Name, StringComparer.OrdinalIgnoreCase)
            .Select(leaf => TreeWalk.ChainToRoot(leaf, ParentOf).Select(t => t.Name).Reverse().ToList())
            .ToList();
        return new Names(trunk, members.Select(m => m.Name).ToList(), chains,
            HasAnchorPool: true, weatherThreshold, config);
    }
}
