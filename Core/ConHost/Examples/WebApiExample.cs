using ConHost;
using ConHost.Examples;
using ConHost.Extensions;
using Microsoft.Extensions.Logging;

namespace ConHost.Examples;

/// <summary>
/// Example showing how to combine workers with WebAPI using ConHostEngine extensions.
/// </summary>
public class WebApiExample
{
    /// <summary>
    /// Example using ConHostEngine with WebAPI extension.
    /// </summary>
    public static async Task RunConHostWithWebApiExample()
    {
        Console.WriteLine("ConHost with WebAPI Example");
        Console.WriteLine("Starting ping worker and WebAPI server...");
        Console.WriteLine("WebAPI available at http://localhost:5000");
        Console.WriteLine("Swagger UI at http://localhost:5000/swagger");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        try
        {
            await ConHostEngine.Create()
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .AddWorker<PingWorker>()
                .AddWebApi(webApp =>
                {
                    // Configure additional endpoints
                    webApp.MapGet("/ping-worker-status", () => new
                    {
                        Worker = "PingWorker",
                        Target = "8.8.8.8",
                        Status = "Running",
                        Interval = "5 seconds"
                    });

                    webApp.MapGet("/workers", () => new
                    {
                        Workers = new[]
                        {
                            new { Name = "PingWorker", Type = "Network Monitor", Status = "Active" }
                        }
                    });
                }, "http://localhost:5000")
                .RunAsync();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nApplication stopped by user.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
        }
    }
}