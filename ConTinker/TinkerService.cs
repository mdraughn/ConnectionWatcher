using ConEngine.Network;
using ConInfrastructure;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Threading.Channels;
using ConCore;
using Microsoft.Extensions.Logging;

namespace ConTinker;

public class TinkerService : IHostedService
{
    private readonly ILogger<TinkerService> _log;

    public TinkerService(ILogger<TinkerService> log)
    {
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var resolverFactory = new DomainNameResolverFactory();
            using IDomainNameResolver resolver = resolverFactory.Create(CancellationToken.None);
            string domainName = "cloudflare.com";

            var resultChannel = Channel.CreateUnbounded<PingResult>();
            var resultWriter = resultChannel.Writer;
            var resultReader = resultChannel.Reader;

            DomainResolutionResult hostInfo = await resolver.ResolveAsync(domainName, 5000);

            foreach (IPAddress ipAddress in hostInfo.IPv4Addresses.Concat(hostInfo.IPv6Addresses))
            {
                _log.LogDebug("Resolved {domainName} IP: {ipAddress}",domainName,ipAddress);
            }

            var pingFactory = new PingTransceiverFactory();
            var pingController = new PingController(TimeProvider.System, pingFactory,_log);
            _log.LogDebug("Starting Pinger...");
            Task controller = pingController.Launch(IPAddress.Parse("192.168.99.1"), resultWriter, cancellationToken);
            _log.LogDebug("Pinger started.");

            //using IPingTransceiver pt = pingFactory.Create(IPAddress.Parse("8.8.8.8"), CancellationToken.None);
            //for (int i = 0; i < 5; i++)
            //{
            //    if (i > 0)
            //    {
            //        await Task.Delay(1000); // Wait 1 second between pings
            //    }
            //    PingResult result = await pt.PingAsync(5000);
            //    Console.WriteLine($"Ping to {result.IpAddress} {(result.Success ? "succeeded" : "failed")} in {result.RoundtripTime} ms");
            //}

        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop your service logic here
        return Task.CompletedTask;
    }
}