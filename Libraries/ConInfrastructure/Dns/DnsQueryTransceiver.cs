using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;
using ConCore.Dns;

namespace ConInfrastructure.NameResolution;

/// <summary>
/// DNS probe (spec 2026-10-10 §4.2): one UDP query per sample for the next
/// lookup name, a well-formed answer is the reply. A seeded name must come
/// back with one of its expected addresses — a lying resolver (captive portal,
/// interception) is a miss, not a hit. The timeout races the receive because
/// a dead resolver simply never answers. Messages never use the bare word
/// "Refused", which TargetMonitor counts as a TCP refusal.
/// </summary>
public sealed class DnsQueryTransceiver(IPAddress ipAddress, IDnsLookupNames names,
    CancellationToken cancellationToken, int port = 53) : IPingTransceiver
{
    public IPAddress IpAddress => ipAddress;

    public async Task<PingResult> PingAsync(int timeoutMs)
    {
        var lookup = names.Next(ipAddress);
        if (lookup is null)
            return new PingResult(ipAddress, false, 0, "NoLookupNames");

        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var stopwatch = Stopwatch.StartNew();
        using var udp = new UdpClient(ipAddress.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            byte[] query;
            try
            {
                query = DnsMessage.BuildQuery(id, lookup.Name);
            }
            catch (ArgumentException)
            {
                return new PingResult(ipAddress, false, 0, "BadName"); // a name the wire format cannot carry
            }
            udp.Connect(new IPEndPoint(ipAddress, port));
            await udp.SendAsync(query, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (!DnsMessage.TryParseAnswer(received.Buffer, out var answer))
                    return new PingResult(ipAddress, false, 0, "Malformed");
                if (answer.Id != id)
                    continue; // a late answer to an earlier query: not ours
                return Classify(ipAddress, lookup, answer, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingResult(ipAddress, false, 0, "TimedOut"); // our own timeout token
        }
        catch (SocketException ex)
        {
            // ICMP port-unreachable surfaces here (ConnectionReset): the host is up, nothing listens on 53.
            return new PingResult(ipAddress, false, 0, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PingResult(ipAddress, false, 0, ex.Message);
        }
    }

    internal static PingResult Classify(IPAddress resolver, DnsLookupName lookup, DnsAnswer answer, long elapsedMs)
    {
        if (lookup.HasExpected)
        {
            if (answer.Rcode == DnsRcode.NoError && answer.Addresses.Any(a => lookup.Expected.Contains(a.ToString())))
                return new PingResult(resolver, true, elapsedMs, "Answered");
            var detail = answer.Rcode switch
            {
                DnsRcode.NoError when answer.Addresses.Count > 0 => string.Join(", ", answer.Addresses),
                DnsRcode.NoError => "no address",
                DnsRcode.NxDomain => "NXDOMAIN",
                DnsRcode.ServFail => "SERVFAIL",
                DnsRcode.Refused => "REFUSED",
                var other => $"RCODE {(int)other}",
            };
            return answer.Rcode is DnsRcode.NoError or DnsRcode.NxDomain
                ? new PingResult(resolver, false, 0, $"WrongAnswer: {detail}")
                : Miss(answer.Rcode);
        }
        return answer.Rcode switch
        {
            DnsRcode.NoError => new PingResult(resolver, true, elapsedMs, "Answered"),
            DnsRcode.NxDomain => new PingResult(resolver, true, elapsedMs, "NxDomain"),
            var rcode => Miss(rcode),
        };

        PingResult Miss(DnsRcode rcode) => new(resolver, false, 0, rcode switch
        {
            DnsRcode.ServFail => "ServFail",
            DnsRcode.Refused => "RcodeRefused",
            var other => $"Rcode{(int)other}",
        });
    }

    public void Dispose() { }
}
