using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConHost;

/// <summary>
/// Base class for background workers that provides common functionality.
/// </summary>
public abstract class BaseWorker : BackgroundService
{
    protected readonly ILogger Logger;

    /// <summary>
    /// Initializes a new instance of the ConWorker class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    protected BaseWorker(ILogger logger)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// The main execution method for the worker.
    /// Override this method to implement your worker logic.
    /// </summary>
    /// <param name="stoppingToken">Token to signal when the worker should stop.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Worker {WorkerName} starting", GetType().Name);

        try
        {
            await DoWorkAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("Worker {WorkerName} was cancelled", GetType().Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Worker {WorkerName} encountered an error", GetType().Name);
            throw;
        }
        finally
        {
            Logger.LogInformation("Worker {WorkerName} stopping", GetType().Name);
        }
    }

    /// <summary>
    /// Override this method to implement your worker logic.
    /// </summary>
    /// <param name="stoppingToken">Token to signal when the worker should stop.</param>
    protected abstract Task DoWorkAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Called when the service is starting.
    /// Override this method to perform any initialization logic.
    /// </summary>
    /// <param name="cancellationToken">Token to signal when start should be cancelled.</param>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting worker {WorkerName}", GetType().Name);
        await base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Called when the service is stopping.
    /// Override this method to perform any cleanup logic.
    /// </summary>
    /// <param name="cancellationToken">Token to signal when stop should be cancelled.</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Logger.LogInformation("Stopping worker {WorkerName}", GetType().Name);
        await base.StopAsync(cancellationToken);
    }
}