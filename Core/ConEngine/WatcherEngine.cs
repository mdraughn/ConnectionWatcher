using ConCore;
using ConEngine.Network;
using System;
using System.Net;
using System.Threading.Channels;
using ConInfrastructure;
using Microsoft.Extensions.Logging;

namespace ConEngine;

public class WatcherEngine
{
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingFactory;
    private readonly ILogger<WatcherEngine> _log;
    private readonly ILoggerFactory _loggerFactory;
    private PingManager? _manager;

    public WatcherEngine(TimeProvider timeProvider, IPingTransceiverFactory pingFactory, ILoggerFactory loggerFactory)
    {
        _timeProvider = timeProvider;
        _pingFactory = pingFactory;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _log = _loggerFactory.CreateLogger<WatcherEngine>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            _log.LogInformation("WatcherEngine starting...");

            var resolverFactory = new DomainNameResolverFactory();
            using IDomainNameResolver resolver = resolverFactory.Create(CancellationToken.None);

            var resultChannel = Channel.CreateUnbounded<PingResult>();
            var resultWriter = resultChannel.Writer;
            var resultReader = resultChannel.Reader;

            string domainName = "cloudflare.com";
            DomainResolutionResult hostInfo = await resolver.ResolveAsync(domainName, 5000);

            foreach(IPAddress ipAddress in hostInfo.IPv4Addresses.Concat(hostInfo.IPv6Addresses))
            {
                _log.LogDebug("Resolved {domainName} IP: {ipAddress}", domainName, ipAddress);
            }

            //var pingFactory = PingTransceiverFactorySimulated.CreateUnreliableNetwork();
            _log.LogDebug("Starting manager...");
            
            // Create PingManager with its own properly-typed logger
            var pingManagerLogger = _loggerFactory.CreateLogger<PingManager>();
            _manager = new PingManager(TimeProvider.System, _pingFactory, cancellationToken, _loggerFactory);
            
            _manager.PingResults.Subscribe(HandlePingResult,HandlePingError,HandlePingCompletion);

            _log.LogInformation("Manager started.");
        });
    }

    private void HandlePingResult(PingResult message)
    {
        if(message.Success)
        {
            _log.LogTrace("Reacting to success from {ipAddress}", message.IpAddress);
        } 
        else
        {
            _log.LogTrace("Reacting to lost ping from {ipAddress}", message.IpAddress);
        }
    }

    private void HandlePingCompletion()
    {
        // Nothing for now
    }

    private void HandlePingError(Exception ex)
    {
        _log.LogError(ex, "Error occurred while processing ping result");
    }

    public void StartWatching(IPAddress ipAddress)
    {
        _manager?.GetOrCreateController(ipAddress);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("WatcherEngine stopping...");
        
        _manager?.StopAllControllers();
        _manager?.Dispose();
        
        _log.LogInformation("WatcherEngine stopped");
        
        return Task.CompletedTask;
    }
}