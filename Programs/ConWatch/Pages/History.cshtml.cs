using System.Text.Json;
using ConEngine.Topology;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class HistoryModel(WatcherHostService host) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string SubjectsJson { get; private set; } = "[]";

    public void OnGet()
    {
        var subjects = new List<object>();
        if (host.Engine is { } engine)
        {
            subjects.AddRange(engine.Graph.Targets.Values
                .OrderBy(t => t.Name)
                .Select(t => new { Id = t.Id, Name = t.Name, Kind = "target" }));
            subjects.AddRange(engine.Graph.Pools.Values
                .OrderBy(p => p.Name)
                .Select(p => new { Id = p.Id, Name = p.Name, Kind = "pool" }));
            if (engine.Verdict is not null)
                subjects.Add(new { Id = VerdictCoordinator.SubjectId, Name = "Internet (verdict)", Kind = "verdict" });
        }
        SubjectsJson = JsonSerializer.Serialize(subjects, JsonOptions);
    }
}
