using ConInfrastructure.NameResolution;

namespace ConEngine.Tests;

public class DomainNameResolverDnsTests
{
    [Fact]
    public async Task Cancelled_token_throws_instead_of_reporting_a_failed_lookup()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var resolver = new DomainNameResolverDns(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync("example.invalid", 5000));
    }

    [Fact]
    public async Task Unknown_host_is_a_failed_result_with_a_reason()
    {
        using var resolver = new DomainNameResolverDns(CancellationToken.None);
        var result = await resolver.ResolveAsync("nonexistent.invalid", 5000);
        Assert.False(result.Success);
        Assert.Contains(result.Status, new[] { "Host not found", "No DNS data found", "Temporary DNS failure" });
        Assert.Empty(result.IpAddresses);
    }

    [Fact]
    public async Task Localhost_resolves_successfully()
    {
        using var resolver = new DomainNameResolverDns(CancellationToken.None);
        var result = await resolver.ResolveAsync("localhost", 5000);
        Assert.True(result.Success);
        Assert.Equal("Success", result.Status);
        Assert.NotEmpty(result.IpAddresses);
        Assert.All(result.IpAddresses, a => Assert.True(System.Net.IPAddress.IsLoopback(a)));
    }
}
