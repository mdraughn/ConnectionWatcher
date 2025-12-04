using ConCore;
using ConEngine.Network;
using ConEngine.Tracking;
using ConEngine.Configuration;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using ConInfrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConEngine;

public class WatcherEngine
{
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingFactory;
    private readonly ILogger<WatcherEngine> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HostTrackingOptions _trackingOptions;
    private PingManager? _manager;
    
    // Dictionary to track information about every host we are pinging
    // Using ConcurrentDictionary for thread-safe access
    private readonly ConcurrentDictionary<IPAddress, HostTracker> _hostTrackers;

    public WatcherEngine(
        TimeProvider timeProvider, 
        IPingTransceiverFactory pingFactory, 
        ILoggerFactory loggerFactory,
        IOptions<HostTrackingOptions> trackingOptions)
    {
        _timeProvider = timeProvider;
        _pingFactory = pingFactory;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _trackingOptions = trackingOptions?.Value ?? throw new ArgumentNullException(nameof(trackingOptions));
        _log = _loggerFactory.CreateLogger<WatcherEngine>();
        _hostTrackers = new ConcurrentDictionary<IPAddress, HostTracker>();
        
        _log.LogInformation("WatcherEngine initialized with HistorySize={HistorySize}, MinUsableSample={MinUsableSample}",
            _trackingOptions.HistorySize, _trackingOptions.MinUsableSample);
    }

    /// <summary>
    /// Gets the number of hosts currently being tracked.
    /// </summary>
    public int TrackedHostCount => _hostTrackers.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            _log.LogInformation("WatcherEngine starting...");

            var resolverFactory = new DomainNameResolverFactory();
            using IDomainNameResolver resolver = resolverFactory.Create(CancellationToken.None);

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
            
            _manager.PingResults.Subscribe(HandlePingResult, HandlePingError, HandlePingCompletion);

            _log.LogInformation("Manager started. Tracking {TrackedCount} hosts.", _hostTrackers.Count);
        }, cancellationToken);
    }

    private void HandlePingResult(PingResult message)
    {
        // Get or create tracker for this host
        var tracker = _hostTrackers.GetOrAdd(message.IpAddress, 
            ipAddress => new HostTracker(ipAddress, _trackingOptions));

        // Record the ping result in the tracker
        tracker.RecordPingResult(message);
    }

    private void HandlePingCompletion()
    {
        _log.LogInformation("Ping result stream completed");
    }

    private void HandlePingError(Exception ex)
    {
        _log.LogError(ex, "Error occurred while processing ping result");
    }

    public void StartWatching(IPAddress ipAddress)
    {
        // Create a tracker for this host when we start watching it
        _hostTrackers.GetOrAdd(ipAddress, 
            ip => new HostTracker(ip, _trackingOptions));
        
        _manager?.GetOrCreateController(ipAddress);
        
        _log.LogInformation("Started watching {ipAddress}. Total tracked hosts: {trackedCount}",
            ipAddress, _hostTrackers.Count);
    }

    public void StopWatching(IPAddress ipAddress)
    {
        _manager?.StopController(ipAddress);
        
        // Optionally remove from tracking (or keep historical data)
        // For now, we'll keep the tracker but you could remove it:
        // _hostTrackers.TryRemove(ipAddress, out _);
        
        _log.LogInformation("Stopped watching {ipAddress}. Total tracked hosts: {trackedCount}",
            ipAddress, _hostTrackers.Count);
    }

    /// <summary>
    /// Removes a host from tracking and clears its history.
    /// </summary>
    /// <param name="hostAddress">The IP address of the host to stop tracking.</param>
    /// <returns>True if the host was being tracked and was removed; otherwise false.</returns>
    public bool RemoveTracking(IPAddress hostAddress)
    {
        if (_hostTrackers.TryRemove(hostAddress, out var _))
        {
            _log.LogInformation("Removed tracking for {ipAddress}. Total tracked hosts: {trackedCount}",
                hostAddress, _hostTrackers.Count);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears all tracking data for all hosts.
    /// </summary>
    public void ClearAllTracking()
    {
        var count = _hostTrackers.Count;
        _hostTrackers.Clear();
        _log.LogInformation("Cleared tracking for all {count} hosts", count);
    }

    public Task StopAsync()
    {
        _log.LogInformation("WatcherEngine stopping...");
        
        _manager?.StopAllControllers();
        _manager?.Dispose();
        
        _log.LogInformation("WatcherEngine stopped.");
        
        return Task.CompletedTask;
    }
}