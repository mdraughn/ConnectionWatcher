using ConCore;
using ConEngine.Network;
using ConInfrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Threading.Channels;
using ConEngine;

namespace ConTinker;

public class TinkerService : IHostedService
{
    private readonly ILogger<TinkerService> _log;
    private WatcherEngine _watcherEngine;

    public TinkerService(ILogger<TinkerService> log, WatcherEngine watcherEngine)
    {
        _log = log;
        _watcherEngine = watcherEngine;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            _log.LogInformation("TinkerService starting...");

            await _watcherEngine.StartAsync(cancellationToken);
            _watcherEngine.StartWatching(IPAddress.Parse("192.168.99.1"));
            //_watcherEngine.StartWatching(IPAddress.Parse("8.8.8.8"));

            _log.LogInformation("TinkerService started successfully");
        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("TinkerService stopping...");
        
        _watcherEngine?.StopAsync();
        
        _log.LogInformation("TinkerService stopped");
        
        return Task.CompletedTask;
    }
}