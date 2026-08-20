using System.Net;
using Microsoft.Extensions.Logging;

namespace ConInfrastructure.Traceroute;

/// <summary>One hop in a traceroute result.</summary>
public record TracerouteHop(int Ttl, string? Address, string? Hostname, long? RttMs, bool ReachedDestination);

/// <summary>
/// TTL-stepped traceroute: 3 probes per hop (best RTT, first answering
/// address), reverse-DNS with a 1-second cap, stops at the destination or
/// TTL 30. One-shot discovery tool, not continuous monitoring.
/// </summary>
public class TracerouteRunner(ITtlProbe probe, ILogger<TracerouteRunner> log)
{
    private const int MaxTtl = 30;
    private const int ProbesPerHop = 3;
    private const int ProbeTimeoutMs = 2000;

    public async Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, CancellationToken ct)
    {
        IPAddress destination;
        if (!IPAddress.TryParse(host, out destination!))
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            destination = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?? throw new ArgumentException($"No IPv4 address for '{host}'.");
        }
        log.LogInformation("Traceroute to {Host} ({Destination})", host, destination);

        var hops = new List<TracerouteHop>();
        for (int ttl = 1; ttl <= MaxTtl; ttl++)
        {
            IPAddress? hopAddress = null;
            long? bestRtt = null;
            bool reached = false;

            for (int i = 0; i < ProbesPerHop; i++)
            {
                var result = await probe.ProbeAsync(destination, ttl, ProbeTimeoutMs, ct).ConfigureAwait(false);
                if (result.Hop is not null)
                {
                    hopAddress ??= result.Hop;
                    bestRtt = bestRtt is { } rtt ? Math.Min(rtt, result.RttMs) : result.RttMs;
                }
                reached |= result.ReachedDestination;
            }

            hops.Add(new TracerouteHop(ttl, hopAddress?.ToString(),
                hopAddress is null ? null : await ReverseDnsAsync(hopAddress, ct).ConfigureAwait(false),
                bestRtt, reached));
            if (reached)
                break;
        }
        return hops;
    }

    private static async Task<string?> ReverseDnsAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address.ToString(), ct);
            var winner = await Task.WhenAny(lookup, Task.Delay(1000, ct)).ConfigureAwait(false);
            return winner == lookup ? (await lookup.ConfigureAwait(false)).HostName : null;
        }
        catch
        {
            return null;
        }
    }
}
