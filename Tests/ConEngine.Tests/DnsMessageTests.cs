using System.Net;
using ConEngine.Tests.Fakes;
using ConInfrastructure.NameResolution;

namespace ConEngine.Tests;

public class DnsMessageTests
{
    [Fact]
    public void BuildQuery_writes_one_A_IN_question_with_recursion_desired()
    {
        var q = DnsMessage.BuildQuery(0x1234, "dns.google");

        Assert.Equal([0x12, 0x34], q[..2]);                 // ID
        Assert.Equal([0x01, 0x00], q[2..4]);                // QR=0, RD=1
        Assert.Equal([0, 1, 0, 0, 0, 0, 0, 0], q[4..12]);   // QDCOUNT 1, rest 0
        Assert.Equal([3, (byte)'d', (byte)'n', (byte)'s', 6, (byte)'g', (byte)'o', (byte)'o', (byte)'g', (byte)'l', (byte)'e', 0],
            q[12..24]);
        Assert.Equal([0, 1, 0, 1], q[24..28]);              // QTYPE A, QCLASS IN
        Assert.Equal(28, q.Length);

        Assert.True(DnsMessage.TryReadQuestionName(q, out var name));
        Assert.Equal("dns.google", name);
    }

    [Fact]
    public void A_NOERROR_response_yields_its_A_records_in_order()
    {
        var bytes = FakeDnsWire.Response(7, "dns.google", DnsRcode.NoError,
            IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"));

        Assert.True(DnsMessage.TryParseAnswer(bytes, out var answer));
        Assert.Equal(7, answer.Id);
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        Assert.Equal([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4")], answer.Addresses);
    }

    [Theory]
    [InlineData(DnsRcode.NxDomain)]
    [InlineData(DnsRcode.ServFail)]
    [InlineData(DnsRcode.Refused)]
    public void An_error_rcode_parses_with_no_addresses(DnsRcode rcode)
    {
        Assert.True(DnsMessage.TryParseAnswer(FakeDnsWire.Response(9, "x.test", rcode), out var answer));
        Assert.Equal(rcode, answer.Rcode);
        Assert.Empty(answer.Addresses);
    }

    [Fact]
    public void An_empty_NOERROR_answer_parses_with_no_addresses()
    {
        Assert.True(DnsMessage.TryParseAnswer(FakeDnsWire.Response(9, "x.test", DnsRcode.NoError), out var answer));
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        Assert.Empty(answer.Addresses);
    }

    [Fact]
    public void A_query_is_not_an_answer()
    {
        Assert.False(DnsMessage.TryParseAnswer(DnsMessage.BuildQuery(1, "x.test"), out _)); // QR=0
    }

    [Fact]
    public void Garbage_short_and_truncated_datagrams_are_malformed_not_exceptions()
    {
        Assert.False(DnsMessage.TryParseAnswer([], out _));
        Assert.False(DnsMessage.TryParseAnswer([1, 2, 3], out _));
        Assert.False(DnsMessage.TryParseAnswer(new byte[64], out _)); // QR=0, so not an answer
        Assert.False(DnsMessage.TryParseAnswer(FakeDnsWire.Truncated(5, "dns.google"), out _));
        var bad = FakeDnsWire.Response(5, "dns.google", DnsRcode.NoError, IPAddress.Parse("8.8.8.8"));
        bad[12] = 0xC0; bad[13] = 0xFF; // name pointer past the end
        Assert.False(DnsMessage.TryParseAnswer(bad, out _));
    }

    [Fact]
    public void Non_A_records_are_skipped()
    {
        // A CNAME before the A record: TYPE 5, RDATA an uncompressed name.
        var bytes = FakeDnsWire.Response(3, "www.test", DnsRcode.NoError, IPAddress.Parse("192.0.2.1")).ToList();
        bytes[7] = 2; // ANCOUNT 2
        var cname = new List<byte> { 0xC0, 12, 0, 5, 0, 1, 0, 0, 0, 60, 0, 6, 4, (byte)'h', (byte)'o', (byte)'s', (byte)'t', 0 };
        var questionEnd = 12 + 1 + 3 + 1 + 4 + 1 + 4; // "www" "test" 0 + QTYPE/QCLASS
        bytes.InsertRange(questionEnd, cname);

        Assert.True(DnsMessage.TryParseAnswer(bytes.ToArray(), out var answer));
        Assert.Equal([IPAddress.Parse("192.0.2.1")], answer.Addresses);
    }

    [Fact]
    public void An_rdlength_longer_than_the_datagram_is_malformed()
    {
        var bytes = FakeDnsWire.Response(5, "dns.google", DnsRcode.NoError, IPAddress.Parse("8.8.8.8"));
        bytes[^6] = 0xFF; bytes[^5] = 0xFF;   // RDLENGTH 0xFFFF

        Assert.False(DnsMessage.TryParseAnswer(bytes, out _));
    }

    [Fact]
    public void A_label_longer_than_the_bytes_left_is_malformed()
    {
        byte[] header = [0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0];   // a response with one question
        var bytes = header.Concat(new byte[] { 63, (byte)'a', (byte)'b', (byte)'c' }).ToArray();   // 63 promised, 3 left

        Assert.False(DnsMessage.TryParseAnswer(bytes, out _));
    }
}
