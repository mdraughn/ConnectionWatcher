using System.Net;
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
