using ConCore.Dns;
using Microsoft.Win32;

namespace ConInfrastructure.NameResolution;

/// <summary>
/// Windows keeps per-interface DoH settings under
/// HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\{iface}\DohInterfaceSettings\Doh\{address}
/// (IPv4 servers) and ...\DohInterfaceSettings\Doh6\{address} (IPv6 servers), each
/// with a DohFlags value that is non-zero when DoH is on for that server. Best effort:
/// any error, or a layout we do not recognise, reads as "nothing configured".
/// </summary>
public sealed class WindowsDohDetector(Func<IEnumerable<string>> dohAddresses) : IDohDetector
{
    private const string Root = @"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters";

    public WindowsDohDetector() : this(ReadRegistry) { }

    public IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses)
    {
        HashSet<string> configured;
        try
        {
            configured = dohAddresses().Select(Canonical).ToHashSet();
        }
        catch (Exception)
        {
            return [];
        }
        return resolverAddresses.Where(a => configured.Contains(Canonical(a)))
            .Select(a => a.ToLowerInvariant()).Distinct().ToList();
    }

    /// <summary>One spelling per address, so an IPv6 server written in full in the
    /// registry (Doh6) still matches the compressed form a resolver reports.</summary>
    private static string Canonical(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) ? ip.ToString() : address.ToLowerInvariant();

    /// <summary>The addresses whose DohFlags is a non-zero integer (int or long).</summary>
    public static IReadOnlyList<string> AddressesFrom(IEnumerable<(string Iface, string Address, object? Flags)> rows) =>
        rows.Where(r => r.Flags is int i && i != 0 || r.Flags is long l && l != 0)
            .Select(r => r.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<string> ReadRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var rows = new List<(string, string, object?)>();
        using var root = Registry.LocalMachine.OpenSubKey(Root);
        if (root is null)
            return [];
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
        return AddressesFrom(rows);
    }
}

public static class DohConfiguration
{
    public static IDohDetector ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsDohDetector() : new NullDohDetector();
}
