using ConPersistence;
using ConWatch.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class DataModel(SettingsStore settingsStore, AdminGate gate) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (gate.IsLocked(User))
            return Redirect("/?admin=1");
        Settings = await settingsStore.GetAllAsync();
        return Page();
    }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
