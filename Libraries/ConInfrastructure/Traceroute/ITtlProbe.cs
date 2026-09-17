using System.Net;

namespace ConInfrastructure.Traceroute;

/// <summary>Result of one TTL-limited probe.</summary>
public record TtlProbeResult(IPAddress? Hop, long RttMs, bool ReachedDestination, bool TimedOut);

/// <summary>One TTL-limited ICMP probe toward a destination.</summary>
public interface ITtlProbe
{
    Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct);
}
