using ConCore;
using ConEngine.Network;
using ConInfrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Threading.Channels;

namespace ConTinker;

public class TinkerService : IHostedService
{
    private TimeProvider _timeProvider;
    private IPingTransceiverFactory _pingFactory;
    private readonly ILogger<TinkerService> _log;
    private PingManager? _manager;

    public TinkerService(ILogger<TinkerService> log, TimeProvider timeProvider, IPingTransceiverFactory pingFactory)
    {
        _log = log;
        _timeProvider = timeProvider;
        _pingFactory = pingFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _manager = new PingManager(_timeProvider, _pingFactory,cancellationToken, _log);

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
            _log.LogDebug("Starting Pinger...");
            //Task controller1 = new PingController(TimeProvider.System, pingFactory, _log).Launch(IPAddress.Parse("192.168.99.1"), resultWriter, cancellationToken);
            //Task controller2 = new PingController(TimeProvider.System, pingFactory, _log).Launch(IPAddress.Parse("8.8.8.8"), resultWriter, cancellationToken);
            _manager = new PingManager(TimeProvider.System, pingFactory, cancellationToken, _log);
            _manager.GetOrCreateController(IPAddress.Parse("192.168.99.1"));
            _manager.GetOrCreateController(IPAddress.Parse("8.8.8.8"));
            _manager.PingResults.Subscribe((message) =>
            {
                if(message.Success)
                {
                    _log.LogTrace("Reacting to success from {ipAddress}", message.IpAddress);
                } else
                {
                    _log.LogTrace("Reacting to lost ping from {ipAddress}", message.IpAddress);
                }
            });
            _log.LogDebug("Pinger started.");
        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop your service logic here
        _manager?.StopAllControllers();
        return Task.CompletedTask;
    }
}