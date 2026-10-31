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
        var sp = _factory.Services;
        Assert.NotNull(sp.GetRequiredService<IGatewayDiscovery>());
        Assert.NotNull(sp.GetRequiredService<IWanAddressProbe>());
        Assert.NotNull(sp.GetRequiredService<DiscoveryStore>());
        Assert.NotNull(sp.GetRequiredService<LanScanner>());
        Assert.NotNull(sp.GetRequiredService<IDiscoveryScanner>());
        Assert.NotNull(sp.GetRequiredService<DiscoverySearchService>());
    }

    [Fact]
    public void Engine_registration_includes_the_doh_detector()
    {
        // ConWatchFactory swaps the detector for a null one, so pin the product registration itself.
        var services = new ServiceCollection();
        services.AddConWatchEngine();
        Assert.Contains(services, d => d.ServiceType == typeof(IDohDetector));
    }
}
