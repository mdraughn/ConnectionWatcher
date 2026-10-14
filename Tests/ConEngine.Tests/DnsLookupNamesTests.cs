using System.Net;
using ConCore.Dns;
using ConInfrastructure.Dns;

namespace ConEngine.Tests;

public class DnsLookupNamesTests
{
    [Fact]
    public void Seeded_names_are_the_four_anchor_operators_with_their_published_addresses()
    {
        var seeded = DnsLookupName.Seeded;
        Assert.Equal(["dns.google", "one.one.one.one", "dns.quad9.net", "dns.opendns.com"], seeded.Select(n => n.Name));
        Assert.Equal(["8.8.8.8", "8.8.4.4"], seeded[0].Expected);
        Assert.Equal(["1.1.1.1", "1.0.0.1"], seeded[1].Expected);
        Assert.Equal(["9.9.9.9", "149.112.112.112"], seeded[2].Expected);
        Assert.Equal(["208.67.222.222", "208.67.220.220"], seeded[3].Expected);
        Assert.All(seeded, n => Assert.True(n.HasExpected));
    }

    [Fact]
    public void Json_round_trips_and_garbage_reads_as_empty()
    {
        var json = DnsLookupName.ToJson(DnsLookupName.Seeded);
        Assert.Contains("\"name\":\"dns.google\"", json);
        Assert.Equal(DnsLookupName.Seeded, DnsLookupName.FromJson(json));

        Assert.Empty(DnsLookupName.FromJson(null));
        Assert.Empty(DnsLookupName.FromJson(""));
        Assert.Empty(DnsLookupName.FromJson("not json"));
        Assert.False(DnsLookupName.TryFromJson("not json", out _));
        Assert.True(DnsLookupName.TryFromJson("[]", out var none));
        Assert.Empty(none);

        var admin = DnsLookupName.FromJson("[{\"name\":\"example.org\"}]");
        Assert.Single(admin);
        Assert.Equal("example.org", admin[0].Name);
        Assert.False(admin[0].HasExpected);
    }

    [Fact]
    public void Blank_names_and_unparsable_expected_addresses_are_dropped()
    {
        var names = DnsLookupName.FromJson("[{\"name\":\"  \"},{\"name\":\"a.test\",\"expected\":[\"1.2.3.4\",\"nope\"]}]");
        Assert.Single(names);
        Assert.Equal(["1.2.3.4"], names[0].Expected);
    }

    [Fact]
    public void Next_rotates_per_resolver_and_is_null_when_empty()
    {
        var names = new DnsLookupNames();
        var a = IPAddress.Parse("10.0.0.1");
        var b = IPAddress.Parse("10.0.0.2");
        Assert.Null(names.Next(a));

        names.Replace([new DnsLookupName("x.test", []), new DnsLookupName("y.test", [])]);
        Assert.Equal("x.test", names.Next(a)!.Name);
        Assert.Equal("y.test", names.Next(a)!.Name);
        Assert.Equal("x.test", names.Next(a)!.Name);
        Assert.Equal("x.test", names.Next(b)!.Name); // b has its own cursor

        names.Replace([new DnsLookupName("z.test", [])]);
        Assert.Equal("z.test", names.Next(a)!.Name); // a shorter list never indexes past the end
        Assert.Equal("z.test", names.Next(a)!.Name);

        names.Replace([]);
        Assert.Null(names.Next(a));
        Assert.Same(DnsLookupNames.Empty, DnsLookupNames.Empty);
        Assert.Null(DnsLookupNames.Empty.Next(a));
    }
}
