using ConCore;
using ConCore.Discovery;
using ConInfrastructure.Traceroute;

namespace ConInfrastructure.Discovery;

/// <summary>One candidate a search found, before it is stored as a discovery.</summary>
public sealed record DiscoveryCandidate(string Address, string? Hostname, DiscoveryKind Kind,
    DiscoveryMethod Method, string? Evidence);

/// <summary>The searches a node can run on its own network vantage. Results always need user confirmation.</summary>
public interface IDiscoveryScanner
{
    Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct);

    /// <summary>The first hop beyond the LAN on the way to <paramref name="host"/>; empty when none was seen.
    /// Throws for an unresolvable host (the caller reports it).</summary>
    Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct);
}

public sealed class DiscoveryScanner(IGatewayDiscovery gateways, TracerouteRunner traceroute) : IDiscoveryScanner
{
    /// <summary>A public anycast address already used as a default anchor; the walk stops long before it.</summary>
    public const string DefaultEdgeHost = "1.1.1.1";

    public async Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct)
    {
        var found = await gateways.DiscoverAsync(ct).ConfigureAwait(false);
        return found
            .Where(g => g.Source == "default-route" || g.Reachable)
            .Select(g => new DiscoveryCandidate(g.Address, null, DiscoveryKind.Gateway,
                g.Source == "default-route" ? DiscoveryMethod.DefaultRoute : DiscoveryMethod.CommonAddress,
                g.Reachable ? (g.RttMs is { } rtt ? $"answers pings in {rtt} ms" : "answers pings") : "the default route, but it does not answer pings"))
            .ToList();
    }

    public async Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct)
    {
        static bool IsPublic(TracerouteHop hop) => hop.Address is { } a && !ProbePolicy.IsPrivateOrCarrier(a);

        var hops = await traceroute.RunAsync(host, lookupOperators: false, stopWhen: IsPublic, ct)
            .ConfigureAwait(false);
        var edge = hops.FirstOrDefault(IsPublic);
        // The destination answering first means no intermediate public hop was seen.
        if (edge is null || edge.ReachedDestination)
            return [];
        return [new DiscoveryCandidate(edge.Address!, edge.Hostname, DiscoveryKind.NetworkEdge,
            DiscoveryMethod.Traceroute,
            $"hop {edge.Ttl} toward {host}" + (edge.RttMs is { } rtt ? $", {rtt} ms" : ""))];
    }
}
