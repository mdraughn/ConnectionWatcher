using ConPersistence.Settings;
using ConWatch.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

/// <summary>Admin-locked by convention (AdminLockPageFilter in ConWatchServices).</summary>
public class SettingsModel(SettingsStore settingsStore, AdminGate gate) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();

    public bool PasswordSet => gate.IsEnabled;
    public bool EngineEnabled => Flag(SettingKeys.EngineEnabled);
    public string CloneSource => Setting(SettingKeys.CloneSourceUrl);

    public async Task OnGetAsync()
    {
        Settings = await settingsStore.GetAllAsync();
    }

    public bool Flag(string key) => SettingRules.ReadFlag(key, Settings.GetValueOrDefault(key));

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
