using ConPersistence;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

/// <summary>Admin-locked by convention (AdminLockPageFilter in Program.cs).</summary>
public class DataModel(SettingsStore settingsStore) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Settings = await settingsStore.GetAllAsync();
    }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
