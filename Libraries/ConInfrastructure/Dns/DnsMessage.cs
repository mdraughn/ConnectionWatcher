using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ConInfrastructure.NameResolution;

public enum DnsRcode { NoError = 0, FormErr = 1, ServFail = 2, NxDomain = 3, NotImp = 4, Refused = 5 }

/// <summary>What a resolver answered: the id it echoed, the RCODE, and every A record.</summary>
public sealed record DnsAnswer(ushort Id, DnsRcode Rcode, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Just enough of RFC 1035 for a reachability probe: build one A/IN query
/// with recursion desired, and read a response's id, RCODE and A records.
/// Every read is bounds-checked; a malformed datagram parses as false, never
/// as an exception. Name compression pointers are followed only to skip.
/// </summary>
public static class DnsMessage
{
    private const int HeaderLength = 12;
    private const ushort TypeA = 1;
    private const ushort ClassIn = 1;

    public static byte[] BuildQuery(ushort id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var labels = name.Trim().TrimEnd('.').Split('.');
        var bytes = new byte[HeaderLength + labels.Sum(l => 1 + Encoding.ASCII.GetByteCount(l)) + 1 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);      // QDCOUNT
        var offset = HeaderLength;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
                throw new ArgumentException($"Invalid DNS label in '{name}'.", nameof(name));
            bytes[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, bytes.AsSpan(offset));
        }
        bytes[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), ClassIn);
        return bytes;
    }

    /// <summary>The first question's name, for a fake server to echo. False when malformed.</summary>
    internal static bool TryReadQuestionName(ReadOnlySpan<byte> datagram, out string name)
    {
        name = "";
        if (datagram.Length < HeaderLength)
            return false;
        var labels = new List<string>();
        var offset = HeaderLength;
        while (true)
        {
            if (offset >= datagram.Length)
                return false;
            int len = datagram[offset++];
            if (len == 0)
                break;
            if ((len & 0xC0) != 0 || offset + len > datagram.Length)
                return false; // compression in a question we built ourselves: not expected
            labels.Add(Encoding.ASCII.GetString(datagram.Slice(offset, len)));
            offset += len;
        }
        name = string.Join('.', labels);
        return true;
    }

    public static bool TryParseAnswer(ReadOnlySpan<byte> datagram, out DnsAnswer answer)
    {
        answer = null!;
        if (datagram.Length < HeaderLength)
            return false;
        var id = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        if ((flags & 0x8000) == 0)
            return false; // QR=0: a query, not a response
        var rcode = (DnsRcode)(flags & 0x000F);
        int qdCount = BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]);
        int anCount = BinaryPrimitives.ReadUInt16BigEndian(datagram[6..]);

        var offset = HeaderLength;
        for (var i = 0; i < qdCount; i++)
        {
            if (!SkipName(datagram, ref offset) || offset + 4 > datagram.Length)
                return false;
            offset += 4; // QTYPE, QCLASS
        }

        var addresses = new List<IPAddress>();
        for (var i = 0; i < anCount; i++)
        {
            if (!SkipName(datagram, ref offset) || offset + 10 > datagram.Length)
                return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(datagram[offset..]);
            var @class = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            int rdLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 8)..]);
            offset += 10;
            if (offset + rdLength > datagram.Length)
                return false;
            if (type == TypeA && @class == ClassIn && rdLength == 4)
                addresses.Add(new IPAddress(datagram.Slice(offset, 4)));
            offset += rdLength;
        }

        answer = new DnsAnswer(id, rcode, addresses);
        return true;
    }

    /// <summary>Advances past a name (labels, or a compression pointer, or both). False on overrun.</summary>
    private static bool SkipName(ReadOnlySpan<byte> datagram, ref int offset)
    {
        while (true)
        {
            if (offset >= datagram.Length)
                return false;
            int len = datagram[offset];
            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 2 > datagram.Length)
                    return false;
                var target = ((len & 0x3F) << 8) | datagram[offset + 1];
                if (target >= datagram.Length)
                    return false; // a pointer past the end
                offset += 2;
                return true;
            }
            offset += 1;
            if (len == 0)
                return true;
            if ((len & 0xC0) != 0 || offset + len > datagram.Length)
                return false;
            offset += len;
        }
    }
}
