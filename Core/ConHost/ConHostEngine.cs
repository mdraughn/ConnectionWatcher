using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConHost;

/// <summary>
/// A simple engine to run workers using the .NET GenericHost capability.
/// </summary>
public class ConHostEngine
{
    private readonly IHostBuilder _hostBuilder;

    /// <summary>
    /// Initializes a new instance of the ConHostEngine.
    /// </summary>
    public ConHostEngine()
    {
        _hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            });
    }

    /// <summary>
    /// Adds a background service/worker to the host.
    /// </summary>
    /// <typeparam name="TService">The type of the background service to add.</typeparam>
    /// <returns>The ConHostEngine instance for method chaining.</returns>
    public ConHostEngine AddWorker<TService>() where TService : class, IHostedService
    {
        _hostBuilder.ConfigureServices(services =>
        {
            services.AddHostedService<TService>();
        });
        return this;
    }

    /// <summary>
    /// Configures services for the host.
    /// </summary>
    /// <param name="configureServices">Action to configure services.</param>
    /// <returns>The ConHostEngine instance for method chaining.</returns>
    public ConHostEngine ConfigureServices(Action<IServiceCollection> configureServices)
    {
        _hostBuilder.ConfigureServices(configureServices);
        return this;
    }

    /// <summary>
    /// Configures logging for the host.
    /// </summary>
    /// <param name="configureLogging">Action to configure logging.</param>
    /// <returns>The ConHostEngine instance for method chaining.</returns>
    public ConHostEngine ConfigureLogging(Action<ILoggingBuilder> configureLogging)
    {
        _hostBuilder.ConfigureLogging(configureLogging);
        return this;
    }

    /// <summary>
    /// Builds and runs the host asynchronously.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A task representing the running host.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var host = _hostBuilder.Build();
        await host.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Builds and runs the host synchronously.
    /// </summary>
    public void Run()
    {
        using var host = _hostBuilder.Build();
        host.Run();
    }

    /// <summary>
    /// Builds the host and returns it for more advanced scenarios.
    /// </summary>
    /// <returns>The built IHost instance.</returns>
    public IHost Build()
    {
        return _hostBuilder.Build();
    }

    /// <summary>
    /// Creates a new ConHostEngine instance.
    /// </summary>
    /// <returns>A new ConHostEngine instance.</returns>
    public static ConHostEngine Create() => new();
}