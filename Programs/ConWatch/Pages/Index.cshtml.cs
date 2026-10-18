using System.Text.Json;
using ConCore.Targets;
using ConEngine.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class IndexModel(WatcherHostService host, ConPersistence.Config.ConfigHealth health,
    Microsoft.EntityFrameworkCore.IDbContextFactory<ConPersistence.ConWatchDbContext> dbFactory,
    ConWatch.Auth.AdminGate gate, ConPersistence.Settings.SettingsStore settingsStore,
    ConPersistence.Config.ConfigStore configStore,
    ConPersistence.Warnings.ModelWarningLedger warnings,
    ConInfrastructure.NameResolution.DnsLookupNames lookupNames) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string InitialDataJson { get; private set; } = "{}";
    public int WarningCount { get; private set; }
    /// <summary>The one page every current warning is fixed on, or null when they differ.</summary>
    public ConPersistence.Config.HealthWarning? WarningPage { get; private set; }
    public bool EngineDisabled { get; private set; }

    public bool ShowLock => gate.IsLocked(User);
    public string ResetPath => gate.FilePath;

    /// <summary>Root monitors for the server-rendered fallback tree.</summary>
    public List<TargetMonitor> Roots { get; } = new();
    public Dictionary<Guid, List<TargetMonitor>> Children { get; } = new();

    /// <summary>LAN devices for the fallback's "How's my LAN" list (never in the tree).</summary>
    public List<TargetMonitor> LocalMonitors { get; } = new();

    /// <summary>Resolver targets for the fallback's "Name resolution" list (never in the tree).</summary>
    public List<TargetMonitor> ResolverMonitors { get; } = new();

    /// <summary>False only when an engine exists and its name-resolution watch is off (true with no engine, so the page never claims the watch is off then). The resolver monitors may still
    /// run (and fail with NoLookupNames), so the fallback shows one line instead of them.</summary>
    public bool DnsWatched { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        // First boot: nothing configured yet -> hand the user to the wizard. System-managed
        // resolver targets appear within a second of first boot and do not count.
        // Checked against the DATABASE, not the engine: right after the wizard
        // applies, the engine may still be mid-rebuild with the old empty graph.
        // Never while locked: Setup would bounce straight back here (loop).
        if (!gate.IsLocked(User))
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .AnyAsync(db.Targets, t => !t.SystemManaged))
                return Redirect("/Setup");
        }

        var healthWarnings = await health.EvaluateAsync();
        WarningCount = healthWarnings.Count;
        WarningPage = healthWarnings.Select(w => w.Page).Distinct().Count() == 1 ? healthWarnings[0] : null;
        var engine = host.Engine;
        var parents = new Dictionary<string, string?>();
        var poolMembers = new Dictionary<string, string[]>();
        DnsWatched = engine?.DnsWatch.Enabled ?? true;

        if (engine is not null)
        {
            foreach (var target in engine.Graph.Targets.Values)
                parents[target.Id.ToString()] = target.ParentId?.ToString();
            foreach (var pool in engine.Graph.Pools.Values)
                poolMembers[pool.Id.ToString()] = pool.MemberTargetIds.Select(id => id.ToString()).ToArray();

            foreach (var monitor in engine.Monitors.Values.OrderBy(m => m.Target.Name))
            {
                if (monitor.Target.Scope == TargetScope.Local)
                {
                    LocalMonitors.Add(monitor);
                    continue;
                }
                if (monitor.Target.Scope == TargetScope.Resolver)
                {
                    ResolverMonitors.Add(monitor); // listed under "Name resolution"; never a tree root
                    continue;
                }
                if (monitor.Target.ParentId is { } parentId && engine.Monitors.ContainsKey(parentId))
                {
                    if (!Children.TryGetValue(parentId, out var list))
                        Children[parentId] = list = new List<TargetMonitor>();
                    list.Add(monitor);
                }
                else
                {
                    Roots.Add(monitor);
                }
            }
        }

        // Topology roles for the punchcard/ledger, from the STORED config so
        // archive mode (no engine) keeps its display attribution.
        var names = await TopologyNames.FromConfigAsync(configStore, settingsStore);
        EngineDisabled = !host.EngineEnabled;

        InitialDataJson = JsonSerializer.Serialize(new
        {
            Snapshot = StatusSnapshotBuilder.Build(engine, warnings, lookupNames.Current),
            Parents = parents,
            PoolMembers = poolMembers,
            TrunkNames = names.TrunkNames,
            AnchorNames = names.AnchorNames,
            WeatherThreshold = names.WeatherThreshold,
            EngineDisabled,
            Admin = !gate.IsLocked(User),
            CloneSource = await settingsStore.GetAsync(ConPersistence.Settings.SettingKeys.CloneSourceUrl),
        }, JsonOptions);
        return Page();
    }
}
