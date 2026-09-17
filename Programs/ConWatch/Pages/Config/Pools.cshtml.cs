using ConPersistence;
using ConPersistence.Dto;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class PoolsModel(ConfigStore store, ConfigHealth health) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Config = await store.ExportAsync();
        Warnings = await health.EvaluateAsync();
    }
}
