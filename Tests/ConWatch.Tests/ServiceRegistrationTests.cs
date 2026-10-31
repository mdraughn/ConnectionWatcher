using ConCore.Dns;
using ConInfrastructure.Discovery;
using ConPersistence.Discoveries;
using ConWatch.Discoveries;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class ServiceRegistrationTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void Discovery_and_dns_services_resolve()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.NotNull(sp.GetRequiredService<IGatewayDiscovery>());
        Assert.NotNull(sp.GetRequiredService<IWanAddressProbe>());
        Assert.NotNull(sp.GetRequiredService<DiscoveryStore>());
        Assert.NotNull(sp.GetRequiredService<LanScanner>());
        Assert.NotNull(sp.GetRequiredService<IDiscoveryScanner>());
        Assert.NotNull(sp.GetRequiredService<DiscoverySearchService>());
        Assert.NotNull(sp.GetRequiredService<IDohDetector>());
    }
}
