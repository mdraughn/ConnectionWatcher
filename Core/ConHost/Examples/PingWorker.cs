using ConHost;
using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;

namespace ConHost.Examples;

/// <summary>
/// A concrete example worker that pings 8.8.8.8 every 5 seconds and logs the results.
/// Demonstrates how to use ConWorker for real-world network monitoring tasks.
/// </summary>
public class PingWorker : ConWorker
{
    private readonly string _targetHost;
    private readonly TimeSpan _interval;
    private readonly int _timeout;

    /// <summary>
    /// Initializes a new instance of the PingWorker class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="targetHost">The host to ping (defaults to 8.8.8.8).</param>
    /// <param name="interval">The interval between pings (defaults to 5 seconds).</param>
    /// <param name="timeout">The ping timeout in milliseconds (defaults to 5000ms).</param>
    public PingWorker(ILogger<PingWorker> logger, 
                     string targetHost = "8.8.8.8", 
                     TimeSpan interval = default, 
                     int timeout = 5000) 
        : base(logger)
    {
        _targetHost = targetHost;
        _interval = interval == default ? TimeSpan.FromSeconds(5) : interval;
        _timeout = timeout;
    }

    /// <summary>
    /// Performs the ping monitoring work.
    /// </summary>
    /// <param name="stoppingToken">Token to signal when the worker should stop.</param>
    protected override async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        using var ping = new Ping();
        var pingCount = 0;
        var successCount = 0;
        var totalResponseTime = 0L;

        Logger.LogInformation("Starting ping monitoring for {Host} every {Interval} seconds", 
            _targetHost, _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            pingCount++;
            
            try
            {
                var reply = await ping.SendPingAsync(_targetHost, _timeout);
                
                if (reply.Status == IPStatus.Success)
                {
                    successCount++;
                    totalResponseTime += reply.RoundtripTime;
                    
                    Logger.LogInformation("Ping #{Count} to {Host}: Success - {RoundtripTime}ms (Success rate: {SuccessRate:P1})", 
                        pingCount, 
                        _targetHost, 
                        reply.RoundtripTime,
                        (double)successCount / pingCount);
                }
                else
                {
                    Logger.LogWarning("Ping #{Count} to {Host}: Failed - {Status} (Success rate: {SuccessRate:P1})", 
                        pingCount, 
                        _targetHost, 
                        reply.Status,
                        (double)successCount / pingCount);
                }
            }
            catch (PingException ex)
            {
                Logger.LogError("Ping #{Count} to {Host}: Exception - {Message} (Success rate: {SuccessRate:P1})", 
                    pingCount, 
                    _targetHost, 
                    ex.Message,
                    (double)successCount / pingCount);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Ping #{Count} to {Host}: Unexpected error (Success rate: {SuccessRate:P1})", 
                    pingCount, 
                    _targetHost,
                    (double)successCount / pingCount);
            }

            // Log summary statistics every 20 pings
            if (pingCount % 20 == 0 && successCount > 0)
            {
                var avgResponseTime = totalResponseTime / successCount;
                Logger.LogInformation("Summary after {Count} pings: {SuccessRate:P1} success rate, {AvgTime}ms average response time", 
                    pingCount, 
                    (double)successCount / pingCount, 
                    avgResponseTime);
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
        }

        // Final summary
        if (pingCount > 0)
        {
            var finalSuccessRate = (double)successCount / pingCount;
            var avgResponseTime = successCount > 0 ? totalResponseTime / successCount : 0;
            
            Logger.LogInformation("Final ping statistics for {Host}: {Total} pings sent, {Success} successful ({SuccessRate:P1}), {AvgTime}ms average response time", 
                _targetHost, 
                pingCount, 
                successCount, 
                finalSuccessRate,
                avgResponseTime);
        }
    }
}