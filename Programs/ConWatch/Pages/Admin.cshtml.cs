using ConPersistence;
using ConPersistence.Dto;
using ConWatch.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class AdminModel(ConfigStore store, SettingsStore settingsStore, AdminGate gate) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();
    public List<PoolDto> Pools { get; private set; } = new();

    public bool PasswordSet => gate.IsEnabled;

    public async Task<IActionResult> OnGetAsync()
    {
        if (gate.IsLocked(User))
            return Redirect("/?admin=1");
        Settings = await settingsStore.GetAllAsync();
        Pools = (await store.ExportAsync()).Pools;
        return Page();
    }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
