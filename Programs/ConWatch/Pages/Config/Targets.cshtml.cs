using ConPersistence;
using ConPersistence.Dto;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

public class TargetsModel(ConfigStore store, ConfigHealth health) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    // Prefill support for the traceroute assist's "add as target".
    public string? PrefillAddress => Request.Query["address"];
    public string? PrefillName => Request.Query["name"];
    public string? PrefillParent => Request.Query["parent"];

    public async Task OnGetAsync()
    {
        Config = await store.ExportAsync();
        Warnings = await health.EvaluateAsync();
    }
}
