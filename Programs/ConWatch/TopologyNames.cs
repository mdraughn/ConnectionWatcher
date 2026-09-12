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
        foreach (var memberName in pool.Members)
        {
            if (!byName.TryGetValue(memberName, out var member))
                continue;
            anchors.Add(member.Name);
            var parentName = member.Parent;
            for (var depth = 0; depth < 20 && parentName is { Length: > 0 }; depth++)
            {
                if (!byName.TryGetValue(parentName, out var ancestor))
                    break;
                if (!trunk.Contains(ancestor.Name))
                    trunk.Add(ancestor.Name);
                parentName = ancestor.Parent;
            }
        }
        return new Names(trunk, anchors, config);
    }
}
