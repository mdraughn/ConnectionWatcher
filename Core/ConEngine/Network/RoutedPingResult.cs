using ConCore;

namespace ConEngine.Network;

/// <summary>
/// A ping result tagged with the target it was probed for. Results are routed
/// by this id, never by IP address: two targets may share an address (or the
/// same host may be probed by ICMP and by TCP) and each keeps its own stream.
/// </summary>
public sealed record RoutedPingResult(Guid TargetId, PingResult Result);
