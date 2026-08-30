using ConPersistence;
using ConPersistence.Dto;
using ConWatch.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

public class ProfilesModel(ConfigStore store, ConfigHealth health, AdminGate gate) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        if (gate.IsLocked(User))
            return Redirect("/?admin=1");
        Config = await store.ExportAsync();
        Warnings = await health.EvaluateAsync();
        return Page();
    }
}
