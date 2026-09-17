using ConPersistence;
using ConPersistence.Dto;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class TargetsModel(ConfigStore store, ConfigHealth health, SettingsStore settings,
    WatcherHostService host) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    /// <summary>Root→leaf chain of targets above the anchor pool (empty = no pipeline view).</summary>
    public List<TargetDto> Trunk { get; private set; } = [];
    public List<TargetDto> Anchors { get; private set; } = [];
    public Dictionary<string, List<TargetDto>> BranchesByTrunkName { get; private set; } = new();

    // Prefill support for the traceroute assist's "add as target".
    public string? PrefillAddress => Request.Query["address"];
    public string? PrefillName => Request.Query["name"];
    public string? PrefillParent => Request.Query["parent"];

    public async Task OnGetAsync()
    {
        Config = await store.ExportAsync();
        Warnings = [.. await health.EvaluateAsync(), .. RuntimeHealth.RefusalWarnings(host.Engine)];
        ComputePipeline(await settings.GetAsync(SettingKeys.AnchorPool));
    }

    private void ComputePipeline(string? anchorPoolName)
    {
        var pool = Config.Pools.FirstOrDefault(p =>
            p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase));
        if (pool is null)
            return;

        var byName = Config.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Anchors = pool.Members.Where(byName.ContainsKey).Select(m => byName[m]).ToList();

        // Trunk leaf: the pool's parent, or the unique shared parent of its members.
        var memberParents = Anchors.Select(a => a.Parent).Distinct().ToList();
        var leafName = pool.Parent ?? (memberParents.Count == 1 ? memberParents[0] : null);
        if (leafName is null || !byName.TryGetValue(leafName, out var leaf))
            return;

        var trunk = new List<TargetDto>();
        var current = leaf;
        var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (current is not null && guard.Add(current.Name))
        {
            trunk.Add(current);
            current = current.Parent is { } parentName && byName.TryGetValue(parentName, out var p) ? p : null;
        }
        trunk.Reverse(); // root first
        Trunk = trunk;

        var trunkNames = trunk.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchorNames = Anchors.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var node in trunk)
        {
            var branches = Config.Targets
                .Where(t => t.Parent == node.Name && !trunkNames.Contains(t.Name) && !anchorNames.Contains(t.Name))
                .ToList();
            if (branches.Count > 0)
                BranchesByTrunkName[node.Name] = branches;
        }
    }
}
