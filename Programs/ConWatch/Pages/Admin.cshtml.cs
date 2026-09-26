using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;
using ConWatch.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

/// <summary>Admin-locked by convention (AdminLockPageFilter in Program.cs).</summary>
public class AdminModel(ConfigStore store, SettingsStore settingsStore, AdminGate gate) : PageModel
{
    public Dictionary<string, string> Settings { get; private set; } = new();
    public List<PoolDto> Pools { get; private set; } = new();

    public bool PasswordSet => gate.IsEnabled;
    public bool EngineEnabled => Setting(SettingKeys.EngineEnabled, "true") != "false";
    public string CloneSource => Setting(SettingKeys.CloneSourceUrl);

    public async Task OnGetAsync()
    {
        Settings = await settingsStore.GetAllAsync();
        Pools = (await store.ExportAsync()).Pools;
    }

    public string Setting(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;
}
