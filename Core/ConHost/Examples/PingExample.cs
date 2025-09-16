using ConHost;
using ConHost.Examples;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConHost.Examples;

/// <summary>
/// Example program demonstrating how to use ConHostEngine with a concrete PingWorker.
/// This shows a complete working example of monitoring network connectivity.
/// </summary>
public class PingExample
{
    /// <summary>
    /// Main entry point for the ping monitoring example.
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    public static async Task Main(string[] args)
    {
        Console.WriteLine("ConHost Ping Worker Example");
        Console.WriteLine("Press Ctrl+C to stop...");
        Console.WriteLine();

        try
        {
            // Create and configure the host engine
            var engine = ConHostEngine.Create()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .ConfigureServices(services =>
                {
                    // You could configure custom services here if needed
                    // For example, configuration options, HTTP clients, etc.
                })
                .AddWorker<PingWorker>();

            // Run the engine - this will start the ping worker
            await engine.RunAsync();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nPing monitoring stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nUnexpected error: {ex.Message}");
        }
    }
}

/// <summary>
/// Alternative example showing how to customize the PingWorker with different settings.
/// </summary>
public class CustomPingExample
{
    /// <summary>
    /// Example showing how to run multiple ping workers with different configurations.
    /// </summary>
    public static async Task RunMultiplePingWorkersExample()
    {
        Console.WriteLine("Multiple Ping Workers Example");
        Console.WriteLine("Monitoring multiple hosts...");
        Console.WriteLine();

        var engine = ConHostEngine.Create()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices(services =>
            {
                // Register multiple ping workers with different configurations
                services.AddHostedService(provider => 
                    new PingWorker(
                        provider.GetRequiredService<ILogger<PingWorker>>(),
                        "8.8.8.8",      // Google DNS
                        TimeSpan.FromSeconds(5)));
                
                services.AddHostedService(provider => 
                    new PingWorker(
                        provider.GetRequiredService<ILogger<PingWorker>>(),
                        "1.1.1.1",      // Cloudflare DNS
                        TimeSpan.FromSeconds(7)));

                services.AddHostedService(provider => 
                    new PingWorker(
                        provider.GetRequiredService<ILogger<PingWorker>>(),
                        "9.9.9.9",      // Quad9 DNS
                        TimeSpan.FromSeconds(10)));
            });

        await engine.RunAsync();
    }
}