using System.Text.Json;
using ConEngine.Tracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class IndexModel(WatcherHostService host, ConPersistence.ConfigHealth health,
    Microsoft.EntityFrameworkCore.IDbContextFactory<ConPersistence.ConWatchDbContext> dbFactory) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string InitialDataJson { get; private set; } = "{}";
    public int WarningCount { get; private set; }

    /// <summary>Root monitors for the server-rendered fallback tree.</summary>
    public List<TargetMonitor> Roots { get; } = new();
    public Dictionary<Guid, List<TargetMonitor>> Children { get; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        // First boot: nothing configured yet -> hand the user to the wizard.
        // Checked against the DATABASE, not the engine: right after the wizard
        // applies, the engine may still be mid-rebuild with the old empty graph.
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            if (!await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .AnyAsync(db.Targets))
                return Redirect("/Setup");
        }

        WarningCount = (await health.EvaluateAsync()).Count;
        var engine = host.Engine;
        var parents = new Dictionary<string, string?>();
        var poolMembers = new Dictionary<string, string[]>();

        if (engine is not null)
        {
            foreach (var target in engine.Graph.Targets.Values)
                parents[target.Id.ToString()] = target.ParentId?.ToString();
            foreach (var pool in engine.Graph.Pools.Values)
                poolMembers[pool.Id.ToString()] = pool.MemberTargetIds.Select(id => id.ToString()).ToArray();

            foreach (var monitor in engine.Monitors.Values.OrderBy(m => m.Target.Name))
            {
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

        InitialDataJson = JsonSerializer.Serialize(new
        {
            Snapshot = StatusSnapshotBuilder.Build(engine),
            Parents = parents,
            PoolMembers = poolMembers,
        }, JsonOptions);
        return Page();
    }
}
