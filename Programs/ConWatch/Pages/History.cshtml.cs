using System.Text.Json;
using ConEngine.Topology;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class HistoryModel(WatcherHostService host,
    ConPersistence.SettingsStore settingsStore) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string SubjectsJson { get; private set; } = "[]";
    public string TopologyJson { get; private set; } = "{}";

    public async Task OnGetAsync()
    {
        var subjects = new List<object>();
        if (host.Engine is { } engine)
        {
            // Topology roles for the cause-aligned ribbon board: anchors are
            // the verdict pool's members; trunk is their ancestor chain.
            var anchorIds = new HashSet<Guid>();
            var trunkIds = new HashSet<Guid>();
            var anchorPoolName = await settingsStore.GetAsync(ConPersistence.SettingKeys.AnchorPool);
            var pool = anchorPoolName is { Length: > 0 }
                ? engine.Graph.Pools.Values.FirstOrDefault(p =>
                    p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (pool is not null)
            {
                foreach (var memberId in pool.MemberTargetIds)
                {
                    if (!engine.Graph.Targets.TryGetValue(memberId, out var member))
                        continue;
                    anchorIds.Add(member.Id);
                    var parentId = member.ParentId;
                    while (parentId is { } pid && engine.Graph.Targets.TryGetValue(pid, out var ancestor))
                    {
                        trunkIds.Add(ancestor.Id);
                        parentId = ancestor.ParentId;
                    }
                }
            }

            subjects.AddRange(engine.Graph.Targets.Values
                .OrderBy(t => t.Name)
                .Select(t => new
                {
                    Id = t.Id,
                    Name = t.Name,
                    Kind = "target",
                    Parent = t.ParentId is { } pid && engine.Graph.Targets.TryGetValue(pid, out var p)
                        ? p.Name : null,
                    Role = trunkIds.Contains(t.Id) ? "trunk"
                        : anchorIds.Contains(t.Id) ? "anchor" : "other",
                }));
            subjects.AddRange(engine.Graph.Pools.Values
                .OrderBy(p => p.Name)
                .Select(p => new { Id = p.Id, Name = p.Name, Kind = "pool", Parent = (string?)null, Role = "other" }));
            if (engine.Verdict is not null)
                subjects.Add(new
                {
                    Id = VerdictCoordinator.SubjectId, Name = "Internet (verdict)",
                    Kind = "verdict", Parent = (string?)null, Role = "other",
                });
            TopologyJson = JsonSerializer.Serialize(new
            {
                TrunkNames = trunkIds.Select(id => engine.Graph.Targets[id].Name).ToList(),
                AnchorNames = anchorIds.Select(id => engine.Graph.Targets[id].Name).ToList(),
                WeatherThreshold = await settingsStore.GetIntAsync(
                    ConPersistence.SettingKeys.WeatherThreshold, 2),
            }, JsonOptions);
        }
        SubjectsJson = JsonSerializer.Serialize(subjects, JsonOptions);
    }
}
