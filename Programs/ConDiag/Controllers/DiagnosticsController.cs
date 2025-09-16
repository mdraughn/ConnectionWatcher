using Microsoft.AspNetCore.Mvc;
using System.Net.NetworkInformation;

namespace ConDiag.Controllers;

/// <summary>
/// API controller for connection diagnostics operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(ILogger<DiagnosticsController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get the current status of the diagnostics service.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            Service = "ConDiag Connection Diagnostics",
            Status = "Running",
            Timestamp = DateTime.UtcNow,
            BackgroundWorkers = new[]
            {
                new { Name = "PingWorker", Target = "8.8.8.8", Status = "Active" }
            }
        });
    }

    /// <summary>
    /// Perform an on-demand ping to a specified host.
    /// </summary>
    /// <param name="host">The host to ping (defaults to 8.8.8.8)</param>
    /// <param name="timeout">Timeout in milliseconds (defaults to 5000)</param>
    [HttpGet("ping")]
    public async Task<IActionResult> PingHost([FromQuery] string host = "8.8.8.8", [FromQuery] int timeout = 5000)
    {
        try
        {
            _logger.LogInformation("API ping request for {Host} with timeout {Timeout}ms", host, timeout);

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeout);

            var result = new
            {
                Host = host,
                Status = reply.Status.ToString(),
                Success = reply.Status == IPStatus.Success,
                RoundtripTime = reply.Status == IPStatus.Success ? reply.RoundtripTime : (long?)null,
                Timestamp = DateTime.UtcNow,
                TimeoutMs = timeout
            };

            return Ok(result);
        }
        catch (PingException ex)
        {
            _logger.LogWarning(ex, "Ping failed for {Host}", host);
            return BadRequest(new
            {
                Host = host,
                Error = ex.Message,
                Success = false,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error pinging {Host}", host);
            return StatusCode(500, new
            {
                Host = host,
                Error = "Internal server error",
                Success = false,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Perform multiple pings to test connectivity reliability.
    /// </summary>
    /// <param name="host">The host to ping</param>
    /// <param name="count">Number of pings to send (max 10)</param>
    /// <param name="timeout">Timeout per ping in milliseconds</param>
    [HttpGet("ping-test")]
    public async Task<IActionResult> PingTest(
        [FromQuery] string host = "8.8.8.8", 
        [FromQuery] int count = 4, 
        [FromQuery] int timeout = 5000)
    {
        if (count > 10)
        {
            return BadRequest("Maximum ping count is 10");
        }

        _logger.LogInformation("API ping test for {Host}, {Count} pings", host, count);

        var results = new List<object>();
        var successCount = 0;
        var totalTime = 0L;

        using var ping = new Ping();

        for (int i = 1; i <= count; i++)
        {
            try
            {
                var reply = await ping.SendPingAsync(host, timeout);
                var success = reply.Status == IPStatus.Success;
                
                if (success)
                {
                    successCount++;
                    totalTime += reply.RoundtripTime;
                }

                results.Add(new
                {
                    Sequence = i,
                    Status = reply.Status.ToString(),
                    Success = success,
                    RoundtripTime = success ? reply.RoundtripTime : (long?)null,
                    Timestamp = DateTime.UtcNow
                });

                // Small delay between pings
                if (i < count)
                {
                    await Task.Delay(100);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ping {Sequence} failed for {Host}", i, host);
                results.Add(new
                {
                    Sequence = i,
                    Status = "Error",
                    Success = false,
                    RoundtripTime = (long?)null,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow
                });
            }
        }

        var summary = new
        {
            Host = host,
            TotalPings = count,
            SuccessfulPings = successCount,
            SuccessRate = Math.Round((double)successCount / count * 100, 1),
            AverageResponseTime = successCount > 0 ? Math.Round((double)totalTime / successCount, 1) : (double?)null,
            Results = results,
            Timestamp = DateTime.UtcNow
        };

        return Ok(summary);
    }

    /// <summary>
    /// Get health check endpoint.
    /// </summary>
    [HttpGet("health")]
    public IActionResult GetHealth()
    {
        return Ok(new
        {
            Status = "Healthy",
            Service = "ConDiag API",
            Timestamp = DateTime.UtcNow,
            Version = "1.0.0"
        });
    }
}