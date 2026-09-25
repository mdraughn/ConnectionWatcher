using System.Text.Json;
using ConEngine.Topology;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class HistoryModel(SettingsStore settingsStore, ConfigStore configStore) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string SubjectsJson { get; private set; } = "[]";
    public string TopologyJson { get; private set; } = "{}";

    /// <summary>Subjects and topology roles from the stored configuration in
    /// both modes (engine running or archive): the ribbon's roles never depend
    /// on whether monitoring is on. See <see cref="TopologyNames"/>.</summary>
    public async Task OnGetAsync()
    {
        var names = await TopologyNames.FromConfigAsync(configStore, settingsStore);
        var trunk = names.TrunkNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchors = names.AnchorNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var subjects = new List<object>();
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
        if (names.HasAnchorPool)
            subjects.Add(new
            {
                Id = VerdictCoordinator.SubjectId, Name = $"{VerdictCoordinator.SubjectName} (verdict)",
                Kind = "verdict", Parent = (string?)null, Role = "other",
            });

        SubjectsJson = JsonSerializer.Serialize(subjects, JsonOptions);
        TopologyJson = JsonSerializer.Serialize(new
        {
            TrunkNames = names.TrunkNames,
            AnchorNames = names.AnchorNames,
            WeatherThreshold = names.WeatherThreshold,
        }, JsonOptions);
    }
}
