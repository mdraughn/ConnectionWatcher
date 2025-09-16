# ConHost - Connection Worker Host Engine

A simple library providing an easy way to run background workers using .NET's GenericHost capability.

## Overview

The `ConHostEngine` class provides a fluent API for configuring and running background workers with minimal setup. It wraps the .NET GenericHost functionality to make it simple to create console applications with background services.

## Quick Start

### Basic Usage

```csharp
using ConHost;

// Create and run a simple worker
await ConHostEngine.Create()
    .AddWorker<MyWorker>()
    .RunAsync();
```

### Concrete Example: Network Ping Monitor

Here's a real-world example that pings 8.8.8.8 every 5 seconds:

```csharp
using ConHost;
using ConHost.Examples;
using Microsoft.Extensions.Logging;

Console.WriteLine("Starting ping monitoring...");

await ConHostEngine.Create()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .AddWorker<PingWorker>()
    .RunAsync();
```

**Output:**
```
ConDiag - Connection Diagnostics Tool
Starting ping monitoring for 8.8.8.8...
Press Ctrl+C to stop.

info: ConHost.Examples.PingWorker[0]
      Starting ping monitoring for 8.8.8.8 every 5 seconds
info: ConHost.Examples.PingWorker[0]
      Ping #1 to 8.8.8.8: Success - 378ms (Success rate: 100.0 %)
info: ConHost.Examples.PingWorker[0]
      Ping #2 to 8.8.8.8: Success - 16ms (Success rate: 100.0 %)
info: ConHost.Examples.PingWorker[0]
      Ping #3 to 8.8.8.8: Success - 26ms (Success rate: 100.0 %)
```

### With Custom Configuration

```csharp
using ConHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var engine = ConHostEngine.Create()
    .ConfigureLogging(logging =>
    {
        logging.SetMinimumLevel(LogLevel.Debug);
        logging.AddFile("logs/app.log"); // If using a file logging provider
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<IMyService, MyService>();
        services.Configure<MyWorkerOptions>(options =>
        {
            options.PollingInterval = TimeSpan.FromSeconds(30);
        });
    })
    .AddWorker<MyWorker>()
    .AddWorker<AnotherWorker>();

await engine.RunAsync();
```

## Creating Workers

### Option 1: Inherit from ConWorker (Recommended)

```csharp
using ConHost;
using Microsoft.Extensions.Logging;

public class MyWorker : ConWorker
{
    public MyWorker(ILogger<MyWorker> logger) : base(logger)
    {
    }

    protected override async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Logger.LogInformation("Doing work at {Time}", DateTimeOffset.Now);
            
            // Your work logic here
            await SomeAsyncWork();
            
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
    
    private async Task SomeAsyncWork()
    {
        // Simulate work
        await Task.Delay(100);
    }
}
```

### Real-World Example: Ping Worker

The `PingWorker` class demonstrates a complete network monitoring solution:

```csharp
using ConHost;
using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;

public class PingWorker : ConWorker
{
    private readonly string _targetHost;
    private readonly TimeSpan _interval;
    private readonly int _timeout;

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

    protected override async Task DoWorkAsync(CancellationToken stoppingToken)
    {
        using var ping = new Ping();
        var pingCount = 0;
        var successCount = 0;

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
                    Logger.LogInformation("Ping #{Count} to {Host}: Success - {RoundtripTime}ms (Success rate: {SuccessRate:P1})", 
                        pingCount, _targetHost, reply.RoundtripTime, (double)successCount / pingCount);
                }
                else
                {
                    Logger.LogWarning("Ping #{Count} to {Host}: Failed - {Status}", 
                        pingCount, _targetHost, reply.Status);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Ping #{Count} to {Host}: Error", pingCount, _targetHost);
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
```

### Option 2: Implement IHostedService directly

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class CustomWorker : IHostedService
{
    private readonly ILogger<CustomWorker> _logger;
    private Timer? _timer;

    public CustomWorker(ILogger<CustomWorker> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Custom Worker starting");
        _timer = new Timer(DoWork, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Custom Worker stopping");
        _timer?.Change(Timeout.Infinite, 0);
        _timer?.Dispose();
        return Task.CompletedTask;
    }

    private void DoWork(object? state)
    {
        _logger.LogInformation("Custom work executing at {Time}", DateTimeOffset.Now);
    }
}
```

## Running the Examples

1. **Simple ping monitoring:**
   ```bash
   cd Programs/ConDiag
   dotnet run
   ```

2. **Multiple ping workers:**
   ```csharp
   // See Examples/PingExample.cs for complete implementation
   await CustomPingExample.RunMultiplePingWorkersExample();
   ```

## API Reference

### ConHostEngine

| Method | Description |
|--------|-------------|
| `Create()` | Creates a new ConHostEngine instance |
| `AddWorker<T>()` | Adds a background service/worker to the host |
| `ConfigureServices(Action<IServiceCollection>)` | Configures services for dependency injection |
| `ConfigureLogging(Action<ILoggingBuilder>)` | Configures logging for the host |
| `RunAsync(CancellationToken)` | Builds and runs the host asynchronously |
| `Run()` | Builds and runs the host synchronously |
| `Build()` | Builds and returns the IHost for advanced scenarios |

### ConWorker

Base class for background workers that provides:

- Automatic logging setup
- Graceful start/stop handling
- Exception handling and logging
- Cancellation token management

Override the `DoWorkAsync(CancellationToken)` method to implement your worker logic.

## Examples

See the `Examples` folder for complete working examples:

- `ExampleWorker` - A simple periodic worker demonstrating the basic pattern
- `PingWorker` - A concrete network monitoring worker that pings internet hosts
- `PingExample` - Complete console application showing ping worker usage

## Dependencies

- Microsoft.Extensions.Hosting
- Microsoft.Extensions.Logging
- System.Net.NetworkInformation (for ping functionality)