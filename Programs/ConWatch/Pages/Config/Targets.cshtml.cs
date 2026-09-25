using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class TargetsModel(ConfigStore store, ConfigHealth health, SettingsStore settings,
    WatcherHostService host) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    /// <summary>One root→leaf chain per distinct anchor parent (empty = no pipeline view).</summary>
    public List<List<TargetDto>> TrunkChains { get; private set; } = [];
    public List<TargetDto> Anchors { get; private set; } = [];
    public Dictionary<string, List<TargetDto>> BranchesByTrunkName { get; private set; } = new();

    // Prefill support for the traceroute assist's "add as target".
    public string? PrefillAddress => Request.Query["address"];
    public string? PrefillName => Request.Query["name"];
    public string? PrefillParent => Request.Query["parent"];

    public async Task OnGetAsync()
    {
        var names = await TopologyNames.FromConfigAsync(store, settings);
        Config = names.Config;
        Warnings = [.. await health.EvaluateAsync(), .. RuntimeHealth.RefusalWarnings(host.Engine)];

        var byName = Config.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Anchors = names.AnchorNames.Select(n => byName[n]).ToList();
        TrunkChains = names.TrunkChains.Select(chain => chain.Select(n => byName[n]).ToList()).ToList();

        var trunkNames = names.TrunkNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchorNames = names.AnchorNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.TrunkNames)
        {
            var branches = Config.Targets
                .Where(t => t.Parent == name && !trunkNames.Contains(t.Name) && !anchorNames.Contains(t.Name))
                .ToList();
            if (branches.Count > 0)
                BranchesByTrunkName[name] = branches;
        }
    }
}
