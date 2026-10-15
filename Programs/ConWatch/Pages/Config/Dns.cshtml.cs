using ConCore.Dns;
using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>The name-resolution watch (spec 2026-10-10 §5.3). Admin-locked by
/// convention (AdminLockPageFilter on /Config). Server-rendered from the stored
/// config so a resolver the admin disabled is still listed.</summary>
public class DnsModel(ConfigStore store, ConfigHealth health, SettingsStore settings) : PageModel
{
    public bool Enabled { get; private set; } = true;
    public IReadOnlyList<DnsLookupName> LookupNames { get; private set; } = [];
    public IReadOnlyList<TargetDto> SystemResolvers { get; private set; } = [];
    public IReadOnlyList<TargetDto> UpstreamResolvers { get; private set; } = [];
    public IReadOnlyList<HealthWarning> Warnings { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Enabled = await settings.GetAsync(SettingKeys.DnsEnabled) != "false";
        LookupNames = DnsLookupName.FromJson(await settings.GetAsync(SettingKeys.DnsLookupNames));
        var config = await store.ExportAsync();
        var resolvers = config.Targets.Where(t => t.Scope == TargetScope.Resolver).OrderBy(t => t.Name).ToList();
        SystemResolvers = resolvers.Where(t => t.SystemManaged).ToList();
        UpstreamResolvers = resolvers.Where(t => !t.SystemManaged).ToList();
        Warnings = await health.EvaluateAsync();
    }
}
