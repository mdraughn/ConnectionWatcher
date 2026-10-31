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
            // Windows reports RoundtripTime=0 for TtlExpired replies, so time it ourselves.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var reply = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMs),
                Payload, new PingOptions(ttl, dontFragment: true), ct).ConfigureAwait(false);
            var elapsed = stopwatch.ElapsedMilliseconds;
            var rtt = reply.RoundtripTime > 0 ? reply.RoundtripTime : elapsed;
            return reply.Status switch
            {
                IPStatus.Success => new TtlProbeResult(reply.Address, rtt, true, false),
                IPStatus.TtlExpired or IPStatus.TimeExceeded =>
                    new TtlProbeResult(reply.Address, rtt, false, false),
                _ => new TtlProbeResult(null, 0, false, true),
            };
        }
        catch (PingException)
        {
            return new TtlProbeResult(null, 0, false, true);
        }
    }
}
