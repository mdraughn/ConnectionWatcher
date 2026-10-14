using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConCore.Discovery;

namespace ConInfrastructure.Discovery;

/// <summary>An IPv4 address on one of this machine's up interfaces, with its subnet prefix and gateways.</summary>
public sealed record LocalNetwork(IPAddress Address, int PrefixLength, IReadOnlyList<IPAddress> Gateways);

public interface ILocalNetworks
{
    IReadOnlyList<LocalNetwork> Read();
}

public sealed class SystemLocalNetworks : ILocalNetworks
{
    public IReadOnlyList<LocalNetwork> Read()
    {
        var result = new List<LocalNetwork>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var properties = nic.GetIPProperties();
                var gateways = properties.GatewayAddresses
                    .Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                foreach (var unicast in properties.UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(new LocalNetwork(unicast.Address, unicast.PrefixLength, gateways));
            }
        }
        catch (NetworkInformationException)
        {
            // No interface information: nothing to scan.
        }
        return result;
    }
}

public sealed record LanScanResult(IReadOnlyList<DiscoveryCandidate> Found, bool Complete);

/// <summary>
/// The LAN scan (spec §2): ping-sweep the machine's own private subnet, then read the neighbor
/// table, so devices that ignore pings but answered ARP still show up. Private ranges only, at
/// most the /24 around each interface address, at most <see cref="MaxHosts"/> hosts, bounded
/// concurrency. Cancellation keeps what was found.
/// </summary>
public sealed class LanScanner(ILocalNetworks networks, INeighborTable neighbors, IVendorLookup vendors,
    Func<IPAddress, CancellationToken, Task<long?>> ping,
    Func<IPAddress, CancellationToken, Task<string?>> reverseDns, int concurrency = LanScanner.MaxConcurrency,
    TimeSpan? nameBudget = null)
{
    public const int MaxHosts = 1024;
    public const int MaxConcurrency = 64;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);
    private const int DnsParallelism = 16;
    /// <summary>How long the name lookups may run after a cancelled sweep (names are a nicety).</summary>
    public static readonly TimeSpan DefaultNameBudget = TimeSpan.FromSeconds(3);

    public async Task<LanScanResult> ScanAsync(CancellationToken ct)
    {
        var nets = networks.Read().Where(n => IsRfc1918(n.Address)).ToList();
        // Virtual adapters (WSL, docker bridges) have no default gateway; when any network does, the real
        // LAN is among those, so sweep only them. With no gateway anywhere, an isolated LAN still gets swept.
        if (nets.Any(n => n.Gateways.Count > 0))
            nets = nets.Where(n => n.Gateways.Count > 0).ToList();
        var own = nets.Select(n => n.Address.ToString()).ToHashSet();
        var gateways = nets.SelectMany(n => n.Gateways).Select(g => g.ToString()).ToHashSet();
        var hosts = HostsToSweep(nets).Where(h => !own.Contains(h.ToString())).Take(MaxHosts).ToList();
        var swept = hosts.Select(h => h.ToString()).ToHashSet();

        var answered = new ConcurrentDictionary<string, long>();
        var complete = true;
        try
        {
            await Parallel.ForEachAsync(hosts,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(concurrency, 1, MaxConcurrency), CancellationToken = ct },
                async (host, token) =>
                {
                    try
                    {
                        if (await ping(host, token).ConfigureAwait(false) is { } rtt)
                            answered[host.ToString()] = rtt;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One host that cannot be pinged is just "no answer"; it must not end the scan.
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            complete = false;
        }

        // The sweep fills the ARP cache; read it even after a cancel — it is quick and local.
        var arp = new Dictionary<string, string>();
        foreach (var entry in neighbors.Read())
            if (swept.Contains(entry.Address))
                arp.TryAdd(entry.Address, entry.Mac);

        var addresses = answered.Keys.Union(arp.Keys)
            .Where(a => !own.Contains(a) && !gateways.Contains(a))
            .OrderBy(a => IpToUInt(IPAddress.Parse(a)))
            .ToList();

        // After a cancel the names still get a short budget of their own, so the scan cannot overrun its cap by much.
        using var nameCts = complete ? CancellationTokenSource.CreateLinkedTokenSource(ct) : new CancellationTokenSource(nameBudget ?? DefaultNameBudget);
        var names = await ResolveNamesAsync(addresses, nameCts.Token).ConfigureAwait(false);
        var found = addresses.Select(a =>
        {
            arp.TryGetValue(a, out var mac);
            var hasPing = answered.TryGetValue(a, out var rtt);
            var parts = new List<string>();
            if (mac is not null)
            {
                var vendor = vendors.Describe(mac);
                parts.Add(vendor is null ? $"MAC {mac}" : $"MAC {mac} ({vendor})");
            }
            parts.Add(hasPing ? $"answers pings in {rtt} ms" : "in the ARP table, no ping reply");
            return new DiscoveryCandidate(a, names.GetValueOrDefault(a), DiscoveryKind.LocalDevice,
                mac is not null ? DiscoveryMethod.ArpTable : DiscoveryMethod.PingSweep,
                string.Join("; ", parts));
        }).ToList();
        return new LanScanResult(found, complete);
    }

    private async Task<Dictionary<string, string>> ResolveNamesAsync(List<string> addresses, CancellationToken ct)
    {
        var names = new ConcurrentDictionary<string, string>();
        try
        {
            await Parallel.ForEachAsync(addresses,
                new ParallelOptions { MaxDegreeOfParallelism = DnsParallelism, CancellationToken = ct },
                async (address, token) =>
                {
                    try
                    {
                        if (await reverseDns(IPAddress.Parse(address), token).ConfigureAwait(false) is { Length: > 0 } name)
                            names[address] = name;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A failed lookup is just "no name".
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Names are a nicety; keep what resolved.
        }
        return new Dictionary<string, string>(names);
    }

    /// <summary>
    /// The candidate hosts for these networks: per network the hosts of its subnet, but at most the
    /// /24 around the machine's address (a /16 is never swept whole); a /31 or /32 has none. RFC 1918
    /// only is enforced by the caller. The machine's own address is NOT removed here.
    /// </summary>
    public static IEnumerable<IPAddress> HostsToSweep(IReadOnlyList<LocalNetwork> nets)
    {
        var seen = new HashSet<uint>();
        foreach (var net in nets)
        {
            var prefix = Math.Max(net.PrefixLength, 24);
            if (prefix >= 31)
                continue;
            var address = IpToUInt(net.Address);
            var mask = uint.MaxValue << (32 - prefix);
            var network = address & mask;
            var broadcast = network | ~mask;
            for (var host = network + 1; host < broadcast; host++)
                if (seen.Add(host))
                    yield return UIntToIp(host);
        }
    }

    public static bool IsRfc1918(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static uint IpToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress UIntToIp(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    /// <summary>One ICMP echo with <see cref="PingTimeout"/>; null when there is no reply.</summary>
    public static async Task<long?> IcmpPing(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(address, PingTimeout, cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;       // PingException, SocketException, ...: no answer
        }
    }

    /// <summary>Reverse DNS with a 1 s cap; null when there is no name.</summary>
    public static async Task<string?> ReverseDns(IPAddress address, CancellationToken ct)
    {
        try
        {
            var lookup = System.Net.Dns.GetHostEntryAsync(address.ToString(), ct);
            var winner = await Task.WhenAny(lookup, Task.Delay(1000, ct)).ConfigureAwait(false);
            return winner == lookup ? (await lookup.ConfigureAwait(false)).HostName : null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }
}
