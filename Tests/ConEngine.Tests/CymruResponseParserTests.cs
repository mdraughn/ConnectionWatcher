using System.Net;
using ConInfrastructure.Asn;

namespace ConEngine.Tests;

public class CymruResponseParserTests
{
    [Fact]
    public void Origin_query_name_reverses_the_octets()
    {
        Assert.Equal("4.3.2.1.origin.asn.cymru.com", CymruResponseParser.OriginQueryName(IPAddress.Parse("1.2.3.4")));
    }

    [Fact]
    public void Origin_query_name_rejects_ipv6()
    {
        Assert.Throws<ArgumentException>(() => CymruResponseParser.OriginQueryName(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public void As_query_name()
    {
        Assert.Equal("AS7922.asn.cymru.com", CymruResponseParser.AsQueryName(7922));
    }

    [Fact]
    public void Origin_answer_yields_the_asn()
    {
        Assert.Equal(7922, CymruResponseParser.ParseOrigin("7922 | 73.0.0.0/8 | US | arin | 2005-01-27"));
    }

    [Fact]
    public void Origin_answer_with_two_asns_takes_the_first()
    {
        Assert.Equal(7922, CymruResponseParser.ParseOrigin("7922 33667 | 73.0.0.0/8 | US | arin | 2005-01-27"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("NA | 10.0.0.0/8 | | | ")]
    public void Unparseable_origin_is_null(string? text)
    {
        Assert.Null(CymruResponseParser.ParseOrigin(text));
    }

    [Fact]
    public void As_answer_yields_name_without_country_suffix()
    {
        var parsed = CymruResponseParser.ParseAsName("7922 | US | arin | 2005-01-27 | COMCAST-7922, US");
        Assert.Equal(("COMCAST-7922", "US"), parsed);
    }

    [Fact]
    public void As_answer_with_description_keeps_the_organization_text()
    {
        var parsed = CymruResponseParser.ParseAsName("7922 | US | arin | 2005-01-27 | COMCAST-7922 - Comcast Cable Communications, LLC, US");
        Assert.Equal(("Comcast Cable Communications, LLC", "US"), parsed);
    }

    [Fact]
    public void As_answer_without_country_keeps_the_whole_name()
    {
        var parsed = CymruResponseParser.ParseAsName("64496 | | | | EXAMPLE-NET");
        Assert.Equal(("EXAMPLE-NET", null), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("7922 | US")]
    [InlineData("7922 | US | arin | 2005-01-27 |   ")]
    public void Unparseable_as_answer_is_null(string? text)
    {
        Assert.Null(CymruResponseParser.ParseAsName(text));
    }
}
