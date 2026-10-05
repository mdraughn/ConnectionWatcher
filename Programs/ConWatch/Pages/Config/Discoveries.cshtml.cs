using ConCore.Discovery;
using ConPersistence.Config;
using ConPersistence.Discoveries;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages.Config;

/// <summary>Admin-locked by convention (AdminLockPageFilter on /Config).</summary>
public class DiscoveriesModel(DiscoveryStore store, ConfigStore configStore) : PageModel
{
    public string StatusFilter { get; private set; } = "new";
    public List<DiscoveryDto> Items { get; private set; } = [];
    private HashSet<string> _targetNames = new(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync(string? status)
    {
        if (!DiscoveryStatuses.TryParseFilter(status, out var filter))
        {
            status = null;
            DiscoveryStatuses.TryParseFilter(null, out filter);
        }
        StatusFilter = string.IsNullOrWhiteSpace(status) ? "new" : status.Trim().ToLowerInvariant();
        Items = await store.ListAsync(filter);
        _targetNames = (await configStore.ExportAsync()).Targets
            .Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string KindLabel(DiscoveryKind kind) => kind switch
    {
        DiscoveryKind.Gateway => "Router (gateway)",
        DiscoveryKind.NetworkEdge => "Network edge (first hop beyond your LAN)",
        _ => "Local device",
    };

    public static string MethodLabel(DiscoveryMethod method) => method switch
    {
        DiscoveryMethod.DefaultRoute => "default route",
        DiscoveryMethod.CommonAddress => "common router address",
        DiscoveryMethod.Traceroute => "traceroute",
        DiscoveryMethod.ArpTable => "ARP table",
        _ => "ping sweep",
    };

    private string UniqueName(string wanted)
    {
        wanted = wanted.Trim();
        if (wanted.Length > 64)
            wanted = wanted[..64];
        if (!_targetNames.Contains(wanted))
            return wanted;
        for (var n = 2; ; n++)
            if (!_targetNames.Contains($"{wanted} {n}"))
                return $"{wanted} {n}";
    }

    /// <summary>The Targets dialog, pre-filled for this discovery; saving it adopts the discovery.</summary>
    public string AdoptUrl(DiscoveryDto d)
    {
        var (name, profile, scope) = d.Kind switch
        {
            DiscoveryKind.Gateway => (UniqueName("Router"), "Internal", "Verdict"),
            DiscoveryKind.NetworkEdge => (UniqueName("ISP edge"), "Path", "Verdict"),
            _ => (UniqueName(string.IsNullOrWhiteSpace(d.Hostname) ? d.Address : d.Hostname), "Local", "Local"),
        };
        var query = new QueryString()
            .Add("address", d.Address).Add("name", name).Add("profile", profile)
            .Add("scope", scope).Add("discoveryId", d.Id.ToString());
        return "/Config/Targets" + query;
    }
}
