# Interactive Ping Simulation

This document describes the interactive ping simulation feature that allows real-time control and monitoring of simulated network behavior.

## Overview

The interactive ping simulation provides a WPF-based control panel that runs on a separate UI thread, allowing you to:
- Monitor all IP addresses being pinged
- Manually trigger outages for specific IP addresses
- Adjust simulation parameters in real-time
- View ping statistics and results

## Architecture

The system consists of several components:

### Core Components

1. **PingTransceiverInteractiveSimFactory** - Factory that creates interactive ping transceivers and initializes the UI
2. **PingTransceiverInteractiveSim** - Simulated ping transceiver that integrates with the UI
3. **SimulationControlManager** - Manages the UI window on a separate STA thread
4. **SimulationControlWindow** - Main WPF window displaying all active simulators

### UI Components

1. **PingSimulatorControl** - Custom control for each IP address showing:
   - IP Address
   - Outage toggle button (red = start outage, teal = stop outage)
   - Settings button to configure simulation parameters
   - Total pings received
   - Last result (response time or failure)

2. **SimulationSettingsDialog** - Modal dialog for editing `PingSimulationOptions`:
   - Latency settings (mean, std dev, min, max)
   - Packet loss rates
   - Outage probability and duration
   - Random seed for reproducible tests

### ViewModel

**PingSimulatorViewModel** - Tracks state for each IP address:
- Outage activation state
- Ping statistics
- Last result
- Simulation options

## Usage

### Basic Setup

In your `Program.cs` or service configuration:

```csharp
services.AddSingleton<IPingTransceiverFactory>(sp => 
    PingTransceiverInteractiveSimFactory.CreateUnreliableNetwork());
```

The UI window will automatically appear when the factory is created.

### Using Preset Configurations

```csharp
// Perfect network - no loss, consistent latency
var factory = PingTransceiverInteractiveSimFactory.CreatePerfectNetwork(latencyMs: 10.0);

// Stable network - minimal issues
var factory = PingTransceiverInteractiveSimFactory.CreateStableNetwork();

// Unreliable network - frequent problems
var factory = PingTransceiverInteractiveSimFactory.CreateUnreliableNetwork();
```

### Custom Configuration

```csharp
var factory = PingTransceiverInteractiveSimFactory.CreateCustom()
    .WithLatency(meanMs: 50.0, stdDevMs: 15.0)
    .WithPacketLoss(lossRate: 0.05)  // 5% loss
    .WithOutages(probability: 0.02, meanDurationMs: 30000.0, stdDevMs: 10000.0)
    .WithOutagePacketLoss(lossRate: 1.0)  // 100% loss during outage
    .WithRandomSeed(12345)  // Reproducible results
    .Build();
```

## UI Features

### Outage Control

- **Start Outage** (Red button): Forces 100% packet loss for this IP until stopped
- **Stop Outage** (Teal button): Returns to normal simulation behavior

Manual outages override automatic random outages.

### Settings Dialog

Click the "Settings" button to open a modal dialog where you can adjust:

- **Latency Settings**
  - Mean round-trip time (ms)
  - Standard deviation (ms)
  - Min/max bounds (ms)

- **Packet Loss Settings**
  - Base packet loss rate (0.0 - 1.0)

- **Outage Settings**
  - Outage probability per ping (0.0 - 1.0)
  - Mean outage duration (ms)
  - Outage duration std dev (ms)
  - Min/max outage duration (ms)
  - Packet loss rate during outage (0.0 - 1.0)

- **Random Seed** (optional)
  - Set for reproducible simulations
  - Leave blank for random behavior

### Statistics Display

Each IP address control shows:
- **Pings**: Total number of ping operations
- **Last**: Most recent result
  - "Success: XXms" for successful pings
  - "Failed" for timeouts/losses

## Threading Model

The UI runs on a **separate STA thread** with its own WPF dispatcher. This means:
- ? Zero impact on your console application
- ? No need to understand WPF in your main application
- ? UI is responsive and independent
- ? Thread-safe updates to UI from ping operations

## Implementation Details

### How It Works

1. When `PingTransceiverInteractiveSimFactory` is created:
   - `SimulationControlManager` starts a new STA thread
   - `SimulationControlWindow` is created on that thread
   - Window becomes visible and responsive

2. When `Create(IPAddress)` is called:
   - A `PingSimulatorViewModel` is registered with the manager
   - A new control appears in the UI for that IP
   - The transceiver is linked to the view model

3. During ping operations:
   - Manual outage state is checked from UI
   - Results are reported back to UI via view model
   - Statistics update in real-time

### Cleanup

The factory implements `IDisposable`. When disposed:
- UI dispatcher is shut down gracefully
- Window closes
- UI thread terminates

If registered as a singleton, disposal happens automatically when the host shuts down.

## Example: ConTinker Integration

```csharp
hostBuilder.ConfigureServices((context, services) =>
{
    services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
    services.AddTransient<IDomainNameResolverFactory, DomainNameResolverFactory>();
    
    // Add interactive simulation factory as singleton
    services.AddSingleton<IPingTransceiverFactory>(sp => 
        PingTransceiverInteractiveSimFactory.CreateUnreliableNetwork());
    
    services.AddTransient<WatcherEngine>();
    services.AddHostedService<TinkerService>();
});
```

## Tips

### For Testing
- Use `WithRandomSeed()` for reproducible test scenarios
- Use manual outage buttons to test failure handling
- Adjust packet loss rates to test resilience

### For Demonstrations
- Use `CreateUnreliableNetwork()` to show handling of poor connections
- Toggle outages during demos to show recovery behavior
- Settings dialog allows live tuning during presentations

### For Development
- UI provides visibility into ping operations without logging
- Manual control enables testing edge cases
- Statistics help identify issues quickly

## Limitations

- The UI window cannot be minimized to system tray (modeless, always visible when created)
- Settings changes apply immediately but don't retroactively affect in-flight operations
- Manual outages remain active until explicitly toggled off
- Window must be closed manually (doesn't close with console app unless app disposes factory)

## Future Enhancements

Potential improvements:
- Export statistics to CSV
- Graph latency over time
- Burst packet loss simulation
- Jitter simulation
- Network condition presets (LTE, 3G, satellite, etc.)
