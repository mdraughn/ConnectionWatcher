using System.Net;
using System.Net.NetworkInformation;
using ConInfrastructure.Net;

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

    private readonly Func<IEnumerable<IPAddress>> _defaultRoutes;
    private readonly Func<IPAddress, CancellationToken, Task<long?>> _probe;

    /// <summary>Production wiring: the host's default routes and a 1 s ICMP echo.</summary>
    public GatewayDiscovery() : this(DefaultRouteGateways, IcmpProbeAsync)
    {
    }

    /// <param name="defaultRoutes">IPv4 default gateways of the up, non-loopback interfaces.</param>
    /// <param name="probe">Round-trip ms when the address answers, null when it does not.</param>
    public GatewayDiscovery(Func<IEnumerable<IPAddress>> defaultRoutes,
        Func<IPAddress, CancellationToken, Task<long?>> probe)
    {
        _defaultRoutes = defaultRoutes;
        _probe = probe;
    }

    public async Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var addresses = new Dictionary<string, string>(); // address -> source, in insertion order
        foreach (var gateway in _defaultRoutes())
            addresses.TryAdd(gateway.ToString(), "default-route");
        foreach (var candidate in CommonCandidates)
            addresses.TryAdd(candidate, "probe");

        // Every candidate at once: the wizard's gateway step takes one probe
        // timeout (1 s), not one per candidate.
        var results = await Task.WhenAll(addresses.Select(async pair =>
        {
            var rtt = await _probe(IPAddress.Parse(pair.Key), ct).ConfigureAwait(false);
            return new GatewayCandidate(pair.Key, pair.Value, rtt is not null, rtt);
        })).ConfigureAwait(false);
        return Order(results);
    }

    private static List<GatewayCandidate> Order(IEnumerable<GatewayCandidate> results) => results
        .OrderByDescending(r => r.Source == "default-route")
        .ThenByDescending(r => r.Reachable)
        .ToList();

    private static IEnumerable<IPAddress> DefaultRouteGateways() =>
        HostInterfaces.Read().SelectMany(i => i.Gateways);

    private static async Task<long?> IcmpProbeAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1),
                cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : (long?)null;
        }
        catch (PingException)
        {
            return null; // unreachable
        }
    }
}
