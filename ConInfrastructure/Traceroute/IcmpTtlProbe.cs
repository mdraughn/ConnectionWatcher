using System.Net;
using System.Net.NetworkInformation;

namespace ConInfrastructure.Traceroute;

/// <summary>
/// Real TTL-stepped ICMP probe. A TtlExpired reply names the hop; Success
/// means the destination itself answered.
/// (Linux containers need CAP_NET_RAW for this — see the Phase 4 packaging spec.)
/// </summary>
public class IcmpTtlProbe : ITtlProbe
{
    private static readonly byte[] Payload = "conwatch-traceroute"u8.ToArray();

    public async Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMs),
                Payload, new PingOptions(ttl, dontFragment: true), ct).ConfigureAwait(false);
            return reply.Status switch
            {
                IPStatus.Success => new TtlProbeResult(reply.Address, reply.RoundtripTime, true, false),
                IPStatus.TtlExpired or IPStatus.TimeExceeded =>
                    new TtlProbeResult(reply.Address, reply.RoundtripTime, false, false),
                IPStatus.TimedOut => new TtlProbeResult(null, 0, false, true),
                _ => new TtlProbeResult(null, 0, false, true),
            };
        }
        catch (PingException)
        {
            return new TtlProbeResult(null, 0, false, true);
        }
    }
}
