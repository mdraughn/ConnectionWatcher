using ConCore.Dns;
using Microsoft.Win32;

namespace ConInfrastructure.NameResolution;

/// <summary>What the Windows registry says about DoH, read once per evaluation: the per-interface
/// servers with DoH on, the DNS client policy (DoHPolicy: 1 Prohibit, 2 Allow, 3 Require — the
/// numeric meanings follow Microsoft's policy reference; the option names are documented, the
/// DWORD values come from community write-ups), EnableAutoDoh (2 = automatic DoH for well-known
/// servers) and the admin-added well-known servers.</summary>
public sealed record DohRegistryView(IReadOnlyList<string> PerInterface, int? DohPolicy, int? EnableAutoDoh, IReadOnlyList<string> WellKnown)
{
    public static DohRegistryView Empty { get; } = new([], null, null, []);
}

/// <summary>
/// Which of the host's resolvers Windows reaches over DoH (spec 2026-10-10 §4.5). Three sources:
/// per-interface settings under
/// HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{iface}\DohInterfaceSettings\Doh\{address}
/// (IPv4) and ...\Doh6\{address} (IPv6), each with a DohFlags value that is non-zero when DoH is on;
/// the DNS client policy DoHPolicy under HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient
/// (Require counts every resolver, Allow the well-known ones, Prohibit switches the automatic
/// paths off); and EnableAutoDoh under Dnscache\Parameters, which upgrades well-known public
/// resolvers (Windows' built-in list plus Dnscache\Parameters\DohWellKnownServers) on its own.
/// Best effort: any error, or a layout we do not recognise, reads as "nothing configured".
/// </summary>
public sealed class WindowsDohDetector(Func<DohRegistryView> view) : IDohDetector
{
    private const string InterfaceRoot = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";
    private const string ParametersKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";

    /// <summary>Windows' own list of DoH-capable public resolvers (Cloudflare, Google, Quad9).</summary>
    public static readonly IReadOnlyList<string> BuiltInWellKnown =
    [
        "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001",
        "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844",
        "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9",
    ];

    public WindowsDohDetector() : this(ReadRegistry) { }

    public IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses)
    {
        try
        {
            return Decide(view(), resolverAddresses);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The resolvers the registry view says Windows reaches over DoH, in the
    /// resolver's own spelling (lower-cased). Prohibit: none — the policy is authoritative for
    /// the DNS client, so a leftover per-interface entry must not raise a false warning.
    /// Require: all of them. Otherwise a per-interface entry, or — under Allow or auto-DoH —
    /// a well-known server.</summary>
    public static IReadOnlyList<string> Decide(DohRegistryView view, IEnumerable<string> resolverAddresses)
    {
        if (view.DohPolicy == 1)
            return [];
        var perInterface = view.PerInterface.Select(Canonical).ToHashSet();
        var require = view.DohPolicy == 3;
        var autoForWellKnown = view.DohPolicy == 2 || view.EnableAutoDoh == 2;
        var wellKnown = BuiltInWellKnown.Concat(view.WellKnown).Select(Canonical).ToHashSet();
        return resolverAddresses
            .Where(a =>
            {
                var c = Canonical(a);
                return require || perInterface.Contains(c) || (autoForWellKnown && wellKnown.Contains(c));
            })
            .Select(a => a.ToLowerInvariant()).Distinct().ToList();
    }

    /// <summary>One spelling per address: zone id stripped (a registry key may carry it),
    /// IPv6 compressed, else lower-cased.</summary>
    private static string Canonical(string address)
    {
        var bare = address.Split('%')[0];
        return System.Net.IPAddress.TryParse(bare, out var ip) ? ip.ToString() : bare.ToLowerInvariant();
    }

    /// <summary>A registry integer read: a DWORD (int) or QWORD (long); anything else is not one.</summary>
    public static int? AsInt(object? value) => value switch { int i => i, long l => (int)l, _ => null };

    /// <summary>The addresses whose DohFlags is a non-zero integer (int or long).</summary>
    public static IReadOnlyList<string> AddressesFrom(IEnumerable<(string Iface, string Address, object? Flags)> rows) =>
        rows.Where(r => r.Flags is int i && i != 0 || r.Flags is long l && l != 0)
            .Select(r => r.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static DohRegistryView ReadRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return DohRegistryView.Empty;

        var rows = new List<(string, string, object?)>();
        using (var root = Registry.LocalMachine.OpenSubKey(InterfaceRoot))
        {
            if (root is not null)
                foreach (var iface in root.GetSubKeyNames())
                {
                    // Doh holds the IPv4 servers, Doh6 the IPv6 ones; both have the same shape.
                    foreach (var family in (string[])["Doh", "Doh6"])
                    {
                        using var doh = root.OpenSubKey($@"{iface}\DohInterfaceSettings\{family}");
                        if (doh is null)
                            continue;
                        foreach (var address in doh.GetSubKeyNames())
                        {
                            using var server = doh.OpenSubKey(address);
                            rows.Add((iface, address, server?.GetValue("DohFlags")));
                        }
                    }
                }
        }

        using var policyKey = Registry.LocalMachine.OpenSubKey(PolicyKey);
        var policy = AsInt(policyKey?.GetValue("DoHPolicy"));
        using var parameters = Registry.LocalMachine.OpenSubKey(ParametersKey);
        var auto = AsInt(parameters?.GetValue("EnableAutoDoh"));
        var wellKnown = new List<string>();
        using var wk = parameters?.OpenSubKey("DohWellKnownServers");
        if (wk is not null)
            wellKnown.AddRange(wk.GetSubKeyNames());
        return new DohRegistryView(AddressesFrom(rows), policy, auto, wellKnown);
    }
}

public static class DohConfiguration
{
    public static IDohDetector ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsDohDetector() : new NullDohDetector();
}
