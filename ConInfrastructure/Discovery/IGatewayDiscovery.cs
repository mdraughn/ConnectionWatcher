namespace ConInfrastructure.Discovery;

/// <summary>One candidate default gateway. Source: "default-route" or "probe".</summary>
public record GatewayCandidate(string Address, string Source, bool Reachable, long? RttMs);

/// <summary>Finds likely home-router addresses. Results always need user confirmation.</summary>
public interface IGatewayDiscovery
{
    Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct);
}
