using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class PoolsModel(ConfigStore store, ConWatch.Health.HealthReport health, SettingsStore settings) : PageModel
{
    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];
    /// <summary>The Verdict.AnchorPool setting; null or empty means the verdict layer is off.</summary>
    public string? AnchorPool { get; private set; }
    public int WeatherThreshold { get; private set; }

    public bool IsAnchor(PoolDto pool) =>
        AnchorPool is { Length: > 0 } && pool.Name.Equals(AnchorPool, StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync()
    {
        Config = await store.ExportAsync();
        Warnings = await health.ForPagesAsync();
        AnchorPool = await settings.GetAsync(SettingKeys.AnchorPool);
        WeatherThreshold = await settings.GetIntAsync(SettingKeys.WeatherThreshold, SettingsDefaults.WeatherThreshold);
    }
}
