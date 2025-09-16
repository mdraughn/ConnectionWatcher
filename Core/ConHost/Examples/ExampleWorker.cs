using Microsoft.Extensions.Logging;

namespace ConHost.Examples;

/// <summary>
/// Example worker that demonstrates how to use ConWorker base class.
/// This worker performs a simple periodic task.
/// </summary>
public class ExampleWorker : ConWorker
{
    private readonly TimeSpan _delay;

    /// <summary>
    /// Initializes a new instance of the ExampleWorker class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="delay">The delay between work iterations.</param>
    public ExampleWorker(ILogger<ExampleWorker> logger, TimeSpan delay = default) 
        : base(logger)
    {
        _delay = delay == default ? TimeSpan.FromSeconds(5) : delay;
    }

    /// <summary>
    /// Performs the main work of this worker.
    /// </summary>
    /// <param name="stoppingToken">Token to signal when the worker should stop.</param>
    protected override async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        var iteration = 0;
        
        while (!stoppingToken.IsCancellationRequested)
        {
            iteration++;
            Logger.LogInformation("ExampleWorker performing work iteration {Iteration} at {Time}", 
                iteration, DateTimeOffset.Now);

            try
            {
                await Task.Delay(_delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
        }
    }
}