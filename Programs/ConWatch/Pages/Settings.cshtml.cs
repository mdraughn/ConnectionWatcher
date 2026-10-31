using ConPersistence.Settings;
using ConWatch.Auth;

namespace ConWatch.Pages;

/// <summary>Admin-locked by convention (AdminLockPageFilter in ConWatchServices).</summary>
public class SettingsModel(SettingsStore settingsStore, AdminGate gate,
    ConWatch.Health.HealthReport health) : SettingsPageModel(settingsStore)
{
    public IReadOnlyList<ConPersistence.Config.HealthWarning> Warnings { get; private set; } = [];

    public bool PasswordSet => gate.IsEnabled;
    public bool EngineEnabled => Flag(SettingKeys.EngineEnabled);
    public string CloneSource => Setting(SettingKeys.CloneSourceUrl);

    public override async Task OnGetAsync()
    {
        await base.OnGetAsync();
        // Deliberately awaited: a cold clone-source probe can take up to 2 s here (cached 30 s).
        Warnings = await health.AllAsync();
    }

    public bool Flag(string key) => SettingRules.ReadFlag(key, Settings.GetValueOrDefault(key));
}
