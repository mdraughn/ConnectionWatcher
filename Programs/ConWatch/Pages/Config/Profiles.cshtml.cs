using System.Globalization;
using ConEngine.Configuration;
using ConPersistence;
using ConPersistence.Config;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class ProfilesModel(ConfigStore store, ConfigHealth health) : PageModel
{
    /// <summary>The new-profile dialog's starting values, keyed by form field name:
    /// a default <see cref="PingProfileConfig"/>, formatted invariantly for number inputs.</summary>
    public static IReadOnlyDictionary<string, string> DialogDefaults { get; } = BuildDialogDefaults();

    internal static Dictionary<string, string> BuildDialogDefaults()
    {
        var c = new PingProfileConfig();
        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return new Dictionary<string, string>
        {
            ["normalIntervalSeconds"] = F(c.NormalIntervalSeconds),
            ["burstIntervalSeconds"] = F(c.BurstIntervalSeconds),
            ["burstMaxDurationSeconds"] = F(c.BurstMaxDurationSeconds),
            ["pingTimeoutSeconds"] = F(c.PingTimeoutSeconds),
            ["historySize"] = c.HistorySize.ToString(CultureInfo.InvariantCulture),
            ["minUsableSample"] = c.MinUsableSample.ToString(CultureInfo.InvariantCulture),
            ["respondingThreshold"] = F(c.RespondingThreshold),
            ["falteringThreshold"] = F(c.FalteringThreshold),
            ["downThreshold"] = F(c.DownThreshold),
            ["recoveringThreshold"] = F(c.RecoveringThreshold),
        };
    }

    public ConfigDocument Config { get; private set; } = new();
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Config = await store.ExportAsync();
        Warnings = await health.EvaluateAsync();
    }
}
