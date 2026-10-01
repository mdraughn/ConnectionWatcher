using System.Net;
using ConInfrastructure.Asn;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class CymruAsnLookupTests
{
    [Fact]
    public async Task Ipv6_address_is_not_looked_up()
    {
        var lookup = new CymruAsnLookup(NullLogger<CymruAsnLookup>.Instance);
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("2001:db8::1"), CancellationToken.None));
    }

    [Fact]
    public async Task Private_address_is_not_looked_up()
    {
        var lookup = new CymruAsnLookup(NullLogger<CymruAsnLookup>.Instance);
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("10.0.0.1"), CancellationToken.None));
    }
}
