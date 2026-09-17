using System.Text.Json;
using ConCore.Targets;
using ConEngine.Topology;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class HistoryModel(WatcherHostService host,
    ConPersistence.SettingsStore settingsStore,
    ConPersistence.ConfigStore configStore) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string SubjectsJson { get; private set; } = "[]";
    public string TopologyJson { get; private set; } = "{}";

    public async Task OnGetAsync()
    {
        var subjects = new List<object>();
        if (host.Engine is null)
        {
            // Archive mode: the history must stay browsable without an
            // engine — subjects and roles come from the stored config.
            await BuildFromConfigAsync(subjects);
            SubjectsJson = JsonSerializer.Serialize(subjects, JsonOptions);
            return;
        }
        if (host.Engine is { } engine)
        {
            // Topology roles for the cause-aligned ribbon board: anchors are
            // the verdict pool's members; trunk is their ancestor chain.
            var anchorPoolName = await settingsStore.GetAsync(ConPersistence.SettingKeys.AnchorPool);
            var pool = anchorPoolName is { Length: > 0 }
                ? engine.Graph.Pools.Values.FirstOrDefault(p =>
                    p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase))
                : null;
            var members = pool?.MemberTargetIds
                .Select(id => engine.Graph.Targets.GetValueOrDefault(id))
                .OfType<Target>().ToList() ?? [];
            var trunk = TreeWalk.AncestorsOf(members,
                t => t.ParentId is { } pid ? engine.Graph.Targets.GetValueOrDefault(pid) : null);
            var anchorIds = members.Select(m => m.Id).ToHashSet();
            var trunkIds = trunk.Select(t => t.Id).ToHashSet();

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
                TrunkNames = trunk.Select(t => t.Name).ToList(),
                AnchorNames = members.Select(m => m.Name).ToList(),
                WeatherThreshold = await settingsStore.GetIntAsync(
                    ConPersistence.SettingKeys.WeatherThreshold, 2),
            }, JsonOptions);
        }
        SubjectsJson = JsonSerializer.Serialize(subjects, JsonOptions);
    }

    private async Task BuildFromConfigAsync(List<object> subjects)
    {
        var names = await TopologyNames.FromConfigAsync(configStore, settingsStore);
        var trunk = names.TrunkNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchors = names.AnchorNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        subjects.AddRange(names.Config.Targets
            .Where(t => t.Id is not null)
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                Id = t.Id!.Value,
                Name = t.Name,
                Kind = "target",
                Parent = t.Parent,
                Role = trunk.Contains(t.Name) ? "trunk"
                    : anchors.Contains(t.Name) ? "anchor" : "other",
            }));
        subjects.AddRange(names.Config.Pools
            .Where(p => p.Id is not null)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                Id = p.Id!.Value, Name = p.Name, Kind = "pool",
                Parent = (string?)null, Role = "other",
            }));
        if (anchors.Count > 0)
            subjects.Add(new
            {
                Id = VerdictCoordinator.SubjectId, Name = "Internet (verdict)",
                Kind = "verdict", Parent = (string?)null, Role = "other",
            });
        TopologyJson = JsonSerializer.Serialize(new
        {
            TrunkNames = names.TrunkNames,
            AnchorNames = names.AnchorNames,
            WeatherThreshold = await settingsStore.GetIntAsync(
                ConPersistence.SettingKeys.WeatherThreshold, 2),
        }, JsonOptions);
    }
}
