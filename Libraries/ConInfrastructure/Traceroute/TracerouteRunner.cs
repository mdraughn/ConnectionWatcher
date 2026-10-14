using System.Net;
using ConInfrastructure.Asn;
using Microsoft.Extensions.Logging;

namespace ConInfrastructure.Traceroute;

/// <summary>Where a hop sits: on the user's LAN, on the route beyond it, or the destination itself.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HopKind>))]
public enum HopKind { Internal, Path, Destination }

/// <summary>One hop in a traceroute result. Kind is null for a hop that did not answer;
/// Asn/AsName are filled only when the operator lookup is on and succeeded.</summary>
public record TracerouteHop(int Ttl, string? Address, string? Hostname, long? RttMs, bool ReachedDestination,
    HopKind? Kind, int? Asn, string? AsName);

/// <summary>
/// TTL-stepped traceroute: 3 probes per hop (best RTT, first answering
/// address), reverse-DNS with a 1-second cap, stops at the destination or
/// TTL 30. One-shot discovery tool, not continuous monitoring.
/// </summary>
public class TracerouteRunner(ITtlProbe probe, IAsnLookup asnLookup, ILogger<TracerouteRunner> log)
{
    private const int MaxTtl = 30;
    private const int ProbesPerHop = 3;
    private const int ProbeTimeoutMs = 2000;
    private const int LookupParallelism = 8;

    public Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, CancellationToken ct) =>
        RunAsync(host, lookupOperators: false, ct);

    public Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, bool lookupOperators, CancellationToken ct) =>
        RunAsync(host, lookupOperators, stopWhen: null, ct);

    /// <param name="lookupOperators">When true, every Path/Destination hop with a
    /// public address is sent to <see cref="IAsnLookup"/> after the walk.</param>
    /// <param name="stopWhen">Called with each raw (not yet classified) hop; returning true
    /// ends the walk after that hop. Null walks to the destination or TTL 30.</param>
    public async Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, bool lookupOperators,
        Func<TracerouteHop, bool>? stopWhen, CancellationToken ct)
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

            var hopRow = new TracerouteHop(ttl, hopAddress?.ToString(),
                hopAddress is null ? null : await ReverseDnsAsync(hopAddress, ct).ConfigureAwait(false),
                bestRtt, reached, null, null, null);
            hops.Add(hopRow);
            if (reached || (stopWhen?.Invoke(hopRow) ?? false))
                break;
        }

        var classified = HopClassifier.Classify(hops);
        return lookupOperators ? await WithOperatorsAsync(classified, ct).ConfigureAwait(false) : classified;
    }

    /// <summary>Fills Asn/AsName on Path and Destination hops with public addresses, a few at a time.</summary>
    private async Task<IReadOnlyList<TracerouteHop>> WithOperatorsAsync(IReadOnlyList<TracerouteHop> hops, CancellationToken ct)
    {
        var result = hops.ToArray();
        using var gate = new SemaphoreSlim(LookupParallelism);
        var tasks = result.Select(async (hop, i) =>
        {
            if (hop.Kind is not (HopKind.Path or HopKind.Destination) ||
                !IPAddress.TryParse(hop.Address, out var ip) || ConCore.ProbePolicy.IsPrivateOrCarrier(ip))
                return;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                try
                {
                    if (await asnLookup.LookupAsync(ip, ct).ConfigureAwait(false) is { } info)
                        result[i] = hop with { Asn = info.Asn, AsName = info.Name };
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    log.LogDebug(ex, "Operator lookup for {Address} failed", hop.Address);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
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
