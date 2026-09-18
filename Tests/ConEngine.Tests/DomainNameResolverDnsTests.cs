using ConInfrastructure;

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
        Assert.NotEqual("Success", result.Status);
        Assert.Empty(result.IpAddresses);
    }
}
