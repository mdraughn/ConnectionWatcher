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
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingFactory;
    private readonly ILogger<TinkerService> _log;
    private readonly ILoggerFactory _loggerFactory;
    private WatcherEngine? _watcherEngine;

    public TinkerService(ILogger<TinkerService> log, TimeProvider timeProvider, IPingTransceiverFactory pingFactory, ILoggerFactory loggerFactory)
    {
        _log = log;
        _timeProvider = timeProvider;
        _pingFactory = pingFactory;
        _loggerFactory = loggerFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            _log.LogInformation("TinkerService starting...");

            // Create WatcherEngine with its own properly-typed logger
            _watcherEngine = new WatcherEngine(_timeProvider, _pingFactory, _loggerFactory);

            await _watcherEngine.StartAsync(cancellationToken);

            _log.LogInformation("TinkerService started successfully");
        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("TinkerService stopping...");
        
        _watcherEngine?.StopAsync(cancellationToken);
        
        _log.LogInformation("TinkerService stopped");
        
        return Task.CompletedTask;
    }
}