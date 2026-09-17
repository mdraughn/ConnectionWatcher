using ConCore.Targets;
using ConPersistence;
using ConPersistence.Dto;

namespace ConWatch;

/// <summary>
/// Trunk/anchor names derived from the STORED configuration — the graph the
/// engine would build. Used by pages when no engine is running (archive
/// mode), where display attribution must keep working without live state.
/// </summary>
public static class TopologyNames
{
    public sealed record Names(List<string> TrunkNames, List<string> AnchorNames,
        ConfigDocument Config);

    public static async Task<Names> FromConfigAsync(ConfigStore configStore,
        SettingsStore settingsStore)
    {
        var config = await configStore.ExportAsync();
        var trunk = new List<string>();
        var anchors = new List<string>();

        var anchorPoolName = await settingsStore.GetAsync(SettingKeys.AnchorPool);
        var pool = anchorPoolName is { Length: > 0 }
            ? config.Pools.FirstOrDefault(p =>
                p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (pool is null)
            return new Names(trunk, anchors, config);

        var byName = config.Targets
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var members = pool.Members.Where(byName.ContainsKey).Select(m => byName[m]).ToList();
        anchors.AddRange(members.Select(m => m.Name));
        trunk.AddRange(TreeWalk.AncestorsOf(members,
                t => t.Parent is { Length: > 0 } parent && byName.TryGetValue(parent, out var p) ? p : null)
            .Select(t => t.Name));
        return new Names(trunk, anchors, config);
    }
}
