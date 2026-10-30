using ConCore.Targets;
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
    /// <summary>Anchors with no (known) parent: reached directly, so they sit on no chain.</summary>
    public List<TargetDto> DirectAnchors { get; private set; } = [];
    public Dictionary<string, List<TargetDto>> BranchesByTrunkName { get; private set; } = new();

    /// <summary>Anchors whose parent is <paramref name="name"/>; names match ignoring case, as everywhere else.</summary>
    public IEnumerable<TargetDto> AnchorsUnder(string name) =>
        Anchors.Where(a => string.Equals(a.Parent, name, StringComparison.OrdinalIgnoreCase));

    // Prefill support for the traceroute assist's "add as target".
    public string? PrefillAddress => Request.Query["address"];
    public string? PrefillName => Request.Query["name"];
    public string? PrefillParent => Request.Query["parent"];
    public string? PrefillProfile => Request.Query["profile"];
    public string? PrefillOperator => Request.Query["operator"];
    public string? PrefillScope => Request.Query["scope"];
    /// <summary>Adopting a Gateway discovery pre-checks Critical (spec §4.7).</summary>
    public bool PrefillCritical =>
        string.Equals(Request.Query["critical"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The discovery being adopted (from the Discoveries page); null when absent or malformed.</summary>
    public string? PrefillDiscoveryId =>
        Guid.TryParse(Request.Query["discoveryId"], out var id) ? id.ToString() : null;

    /// <summary>The profile the dialog selects: the prefilled one when it exists, else Local for a
    /// Local-scope prefill, else Public.</summary>
    public string DefaultProfile =>
        Config.Profiles.Any(p => p.Name.Equals(PrefillProfile, StringComparison.OrdinalIgnoreCase))
            ? PrefillProfile!
            : string.Equals(PrefillScope, "Local", StringComparison.OrdinalIgnoreCase) ? TargetRole.LocalDevice.Profile : TargetRole.Anchor.Profile;

    public async Task OnGetAsync()
    {
        var names = await TopologyNames.FromConfigAsync(store, settings);
        Config = names.Config;
        Warnings = [.. await health.EvaluateAsync(), .. RuntimeHealth.RefusalWarnings(host.Engine)];

        var byName = Config.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Anchors = names.AnchorNames.Select(n => byName[n]).ToList();
        TrunkChains = names.TrunkChains.Select(chain => chain.Select(n => byName[n]).ToList()).ToList();
        DirectAnchors = names.DirectAnchorNames.Select(n => byName[n]).ToList();

        var trunkNames = names.TrunkNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchorNames = names.AnchorNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.TrunkNames)
        {
            var branches = Config.Targets
                .Where(t => string.Equals(t.Parent, name, StringComparison.OrdinalIgnoreCase)
                            && !trunkNames.Contains(t.Name) && !anchorNames.Contains(t.Name))
                .ToList();
            if (branches.Count > 0)
                BranchesByTrunkName[name] = branches;
        }
    }
}
