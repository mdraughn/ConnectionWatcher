using System.Net;
using System.Net.Sockets;
using System.Text;
using ConInfrastructure.NameResolution;

namespace ConEngine.Tests.Fakes;

/// <summary>Wire-format helpers written by hand (RFC 1035 §4.1), independent of
/// DnsMessage, so the parser is tested against the standard and not itself.</summary>
public static partial class FakeDnsWire
{
    public static byte[] Response(ushort id, string name, DnsRcode rcode, params IPAddress[] addresses)
    {
        var bytes = new List<byte>();
        void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
        U16(id);
        U16(0x8180 | (int)rcode);         // QR=1, RD=1, RA=1, RCODE
        U16(1);                            // QDCOUNT
        U16(addresses.Length);             // ANCOUNT
        U16(0); U16(0);                    // NSCOUNT, ARCOUNT
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        U16(1); U16(1);                    // QTYPE A, QCLASS IN
        foreach (var address in addresses)
        {
            bytes.Add(0xC0); bytes.Add(12); // NAME: pointer to the question name at offset 12
            U16(1); U16(1);                 // TYPE A, CLASS IN
            bytes.AddRange([0, 0, 0, 60]);  // TTL 60
            U16(4);                         // RDLENGTH
            bytes.AddRange(address.GetAddressBytes());
        }
        return bytes.ToArray();
    }

    /// <summary>A response whose header promises more answers than it carries.</summary>
    public static byte[] Truncated(ushort id, string name) =>
        Response(id, name, DnsRcode.NoError, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"))[..^10];
}

/// <summary>A loopback UDP "resolver" whose reply is scripted per query.</summary>
public sealed class FakeDnsServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private int _received;

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int Received => Volatile.Read(ref _received);

    /// <summary>(query id, question name) → the datagram(s) to send back; null sends nothing.</summary>
    public Func<ushort, string, IEnumerable<byte[]>?> Reply { get; set; } = (id, name) =>
        [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("8.8.8.8"))];

    public FakeDnsServer() => _ = Task.Run(ServeAsync);

    private async Task ServeAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var query = await _udp.ReceiveAsync(_cts.Token);
                Interlocked.Increment(ref _received);
                var id = (ushort)((query.Buffer[0] << 8) | query.Buffer[1]);
                DnsMessage.TryReadQuestionName(query.Buffer, out var name);
                var replies = Reply(id, name);
                if (replies is null)
                    continue;
                foreach (var reply in replies)
                    await _udp.SendAsync(reply, query.RemoteEndPoint, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
