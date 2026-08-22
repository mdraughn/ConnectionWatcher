using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConInfrastructure.Discovery;

/// <summary>
/// Default-route gateways from the host's interfaces plus a probe of common
/// home-router addresses. Inside a container the default route may be a
/// Docker bridge — callers must present, never trust, these candidates.
/// </summary>
public class GatewayDiscovery : IGatewayDiscovery
{
    private static readonly string[] CommonCandidates =
        ["192.168.0.1", "192.168.1.1", "10.0.0.1", "10.0.0.2", "192.168.100.1"];

    public async Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var addresses = new Dictionary<string, string>(); // address -> source
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                    addresses.TryAdd(gateway.Address.ToString(), "default-route");
            }
        }
        foreach (var candidate in CommonCandidates)
            addresses.TryAdd(candidate, "probe");

        var results = new List<GatewayCandidate>();
        foreach (var (address, source) in addresses)
        {
            bool reachable = false;
            long? rtt = null;
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(IPAddress.Parse(address),
                    TimeSpan.FromSeconds(1), cancellationToken: ct).ConfigureAwait(false);
                reachable = reply.Status == IPStatus.Success;
                if (reachable)
                    rtt = reply.RoundtripTime;
            }
            catch (PingException)
            {
                // unreachable
            }
            results.Add(new GatewayCandidate(address, source, reachable, rtt));
        }
        return results
            .OrderByDescending(r => r.Source == "default-route")
            .ThenByDescending(r => r.Reachable)
            .ToList();
    }
}
