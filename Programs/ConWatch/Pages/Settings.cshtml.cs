using ConPersistence.Settings;
using ConWatch.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

/// <summary>Admin-locked by convention (AdminLockPageFilter in ConWatchServices).</summary>
public class SettingsModel(SettingsStore settingsStore, AdminGate gate,
    ConWatch.Health.HealthReport health) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();

    public IReadOnlyList<ConPersistence.Config.HealthWarning> Warnings { get; private set; } = [];

    public bool PasswordSet => gate.IsEnabled;
    public bool EngineEnabled => Flag(SettingKeys.EngineEnabled);
    public string CloneSource => Setting(SettingKeys.CloneSourceUrl);

    public async Task OnGetAsync()
    {
        Settings = await settingsStore.GetAllAsync();
        Warnings = await health.AllAsync();
    }

    public bool Flag(string key) => SettingRules.ReadFlag(key, Settings.GetValueOrDefault(key));

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
