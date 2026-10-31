using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

/// <summary>Base for the admin-locked pages that render stored settings (Settings, Data).</summary>
public abstract class SettingsPageModel(SettingsStore settingsStore) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();

    public virtual async Task OnGetAsync()
    {
        Settings = await settingsStore.GetAllAsync();
    }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
