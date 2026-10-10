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
    public int NewNetworkEdgeCount { get; private set; }
    public int NewLocalDeviceCount { get; private set; }
    private HashSet<string> _targetNames = new(StringComparer.OrdinalIgnoreCase);

    public async Task OnGetAsync(string? status)
    {
        if (!DiscoveryStatuses.TryParseFilter(status, out var filter))
        {
            status = null;
            DiscoveryStatuses.TryParseFilter(null, out filter);
        }
        StatusFilter = string.IsNullOrWhiteSpace(status) ? "new" : status.Trim().ToLowerInvariant();
        if (StatusFilter == "dismissed")
            StatusFilter = "hidden";
        Items = await store.ListAsync(filter);
        var targets = (await configStore.ExportAsync()).Targets;
        _targetNames = targets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _targetByAddress = new(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
            _targetByAddress.TryAdd(t.Address.Trim(), t.Name);

        var all = await store.ListAsync(null);   // every status: the gateway may already be adopted or dismissed
        NewNetworkEdgeCount = all.Count(d => d.Kind == DiscoveryKind.NetworkEdge && d.Status == DiscoveryStatus.New);
        NewLocalDeviceCount = all.Count(d => d.Kind == DiscoveryKind.LocalDevice && d.Status == DiscoveryStatus.New);
        var gateways = all.Where(d => d.Kind == DiscoveryKind.Gateway).OrderByDescending(d => d.LastSeenUtc).ToList();
        _routerName =
            gateways.Where(d => d.Status == DiscoveryStatus.Adopted && d.AdoptedTargetName is not null)
                .Select(d => d.AdoptedTargetName).FirstOrDefault()
            ?? gateways.Select(d => MonitoredAs(d)).FirstOrDefault(n => n is not null);
    }

    private string? _routerName;

    private Dictionary<string, string> _targetByAddress = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name of the existing target monitoring this discovery's address, if any.</summary>
    public string? MonitoredAs(DiscoveryDto d) =>
        _targetByAddress.TryGetValue(d.Address.Trim(), out var name) ? name : null;

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
            .Add("scope", scope);
        // A router is the device you most want to hear about: adopting the
        // gateway pre-checks Critical (spec §4.7). The admin can uncheck it.
        if (d.Kind == DiscoveryKind.Gateway)
            query = query.Add("critical", "true");
        if (d.Kind == DiscoveryKind.NetworkEdge && _routerName is { Length: > 0 })
            query = query.Add("parent", _routerName);
        query = query.Add("discoveryId", d.Id.ToString());
        return "/Config/Targets" + query;
    }
}
