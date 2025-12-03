# Ping Simulation System

A comprehensive simulation framework for testing and demonstrating network monitoring without requiring actual network access or elevated privileges.

## Overview

The ping simulation system provides realistic network behavior simulation including:
- Variable latency with normal distribution
- Configurable packet loss rates
- Probabilistic network outages with configurable duration
- Reproducible simulations via random seed

## Components

### 1. `PingSimulationOptions`

Configuration class controlling all aspects of network behavior simulation.

#### Key Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MeanRoundTripTimeMs` | double | 20.0 | Average ping latency |
| `RoundTripTimeStdDevMs` | double | 5.0 | Latency variation |
| `BasePacketLossRate` | double | 0.01 | Normal packet loss (0.0-1.0) |
| `OutageProbability` | double | 0.001 | Chance of outage per ping |
| `MeanOutageDurationMs` | double | 30000 | Average outage duration |
| `OutageDurationStdDevMs` | double | 10000 | Outage duration variation |
| `OutagePacketLossRate` | double | 1.0 | Packet loss during outage |
| `RandomSeed` | int? | null | Seed for reproducibility |

#### Preset Configurations

```csharp
// Perfect network - no packet loss, consistent latency
var options = PingSimulationOptions.CreatePerfectNetwork(latencyMs: 10.0);

// Stable network - low latency, minimal loss, rare outages
var options = PingSimulationOptions.CreateStableNetwork();

// Unreliable network - high latency, frequent loss and outages
var options = PingSimulationOptions.CreateUnreliableNetwork();
```

### 2. `PingTransceiverSimulated`

Simulated ping transceiver implementing `IPingTransceiver`.

#### Features

- **Normal distribution latency** - Uses Box-Muller transform for realistic timing
- **Packet loss simulation** - Probabilistic packet drops
- **Outage simulation** - Random outages with configurable behavior
- **State inspection** - Query current outage status
- **Async/await support** - Simulates network delays properly

#### Usage

```csharp
var options = new PingSimulationOptions
{
    MeanRoundTripTimeMs = 25.0,
    RoundTripTimeStdDevMs = 8.0,
    BasePacketLossRate = 0.02
};

using var transceiver = new PingTransceiverSimulated(
    IPAddress.Parse("8.8.8.8"),
    cancellationToken,
    options);

var result = await transceiver.PingAsync(5000);
```

### 3. `PingTransceiverFactorySimulated`

Factory for creating simulated transceivers with consistent configuration.

#### Static Factory Methods

```csharp
// Perfect network
var factory = PingTransceiverFactorySimulated.CreatePerfectNetwork(latencyMs: 10);

// Stable network
var factory = PingTransceiverFactorySimulated.CreateStableNetwork();

// Unreliable network
var factory = PingTransceiverFactorySimulated.CreateUnreliableNetwork();
```

#### Builder Pattern

```csharp
var factory = PingTransceiverFactorySimulated.CreateCustom()
    .WithLatency(meanMs: 30, stdDevMs: 10)
    .WithPacketLoss(0.03)
    .WithOutages(probability: 0.01, meanDurationMs: 15000, stdDevMs: 5000)
    .WithOutagePacketLoss(0.95)
    .WithRandomSeed(42)
    .Build();
```

## Statistical Model

### Latency Distribution

Round-trip times follow a **normal (Gaussian) distribution**:
- Mean: `MeanRoundTripTimeMs`
- Standard deviation: `RoundTripTimeStdDevMs`
- Bounds: Clamped to `[MinRoundTripTimeMs, MaxRoundTripTimeMs]`

Generated using Box-Muller transform:
```
z = ?(-2 ln(u?)) * sin(2? * u?)
RTT = ? + ? * z
```

### Packet Loss Model

Two-state model:
1. **Normal operation**: Loss rate = `BasePacketLossRate`
2. **During outage**: Loss rate = `OutagePacketLossRate`

Packet loss is determined per-ping using uniform random:
```
isLost = random() < currentLossRate
```

### Outage Model

Outages occur probabilistically:
- **Trigger**: Each ping has `OutageProbability` chance of starting outage
- **Duration**: Normal distribution with mean/stddev
- **State**: Only one outage at a time
- **Recovery**: Automatic after duration expires

## Use Cases

### 1. Development & Testing

Test monitoring systems without network access:

```csharp
var factory = PingTransceiverFactorySimulated.CreateStableNetwork();
var manager = new PingManager(timeProvider, factory, cancellationToken);

// Test your monitoring logic
var controller = manager.GetOrCreateController(IPAddress.Parse("8.8.8.8"));
await controller.Start(IPAddress.Parse("8.8.8.8"));
```

### 2. Demonstrations

Show system behavior with different network conditions:

```csharp
// Demo perfect conditions
ShowMonitoring(PingTransceiverFactorySimulated.CreatePerfectNetwork());

// Demo with problems
ShowMonitoring(PingTransceiverFactorySimulated.CreateUnreliableNetwork());
```

### 3. Performance Testing

Benchmark with reproducible results:

```csharp
var factory = PingTransceiverFactorySimulated.CreateCustom()
    .WithRandomSeed(12345) // Reproducible
    .WithLatency(100, 20)
    .Build();

// Run benchmarks with consistent simulated data
```

### 4. UI Development

Develop UIs without live data:

```csharp
// Simulate various scenarios for UI testing
var scenarios = new[] {
    ("Good", PingTransceiverFactorySimulated.CreatePerfectNetwork()),
    ("Average", PingTransceiverFactorySimulated.CreateStableNetwork()),
    ("Poor", PingTransceiverFactorySimulated.CreateUnreliableNetwork())
};
```

## Advanced Features

### Outage Detection

Check if simulation is currently in an outage:

```csharp
var transceiver = factory.Create(ipAddress, cancellationToken) as PingTransceiverSimulated;
var state = transceiver.GetSimulationState();

if (state.IsInOutage)
{
    Console.WriteLine($"Outage: {state.OutageRemainingTime?.TotalSeconds:F1}s remaining");
}
```

### Reproducible Simulations

Use random seed for deterministic behavior:

```csharp
var options = new PingSimulationOptions
{
    RandomSeed = 42, // Same seed = same results
    // ... other options
};
```

### Custom Network Profiles

Create specific scenarios:

```csharp
// Simulate intercontinental link
var factory = PingTransceiverFactorySimulated.CreateCustom()
    .WithLatency(meanMs: 200, stdDevMs: 50)
    .WithPacketLoss(0.001)
    .Build();

// Simulate congested network
var factory = PingTransceiverFactorySimulated.CreateCustom()
    .WithLatency(meanMs: 100, stdDevMs: 80)
    .WithPacketLoss(0.05)
    .WithOutages(0.02, 5000, 2000)
    .Build();
```

## Best Practices

### 1. Validation

Always validate options before use:

```csharp
var options = new PingSimulationOptions { /* ... */ };
options.Validate(); // Throws if invalid
```

### 2. Resource Management

Always dispose transceivers:

```csharp
using var transceiver = factory.Create(ipAddress, cancellationToken);
// Use transceiver
```

### 3. Realistic Values

Choose realistic parameters:
- **LAN**: 1-10ms mean latency
- **WAN**: 20-100ms mean latency
- **Intercontinental**: 150-300ms mean latency
- **Packet loss**: 0.001-0.05 for most networks

### 4. Testing Multiple Scenarios

Test edge cases:

```csharp
var testCases = new[]
{
    PingSimulationOptions.CreatePerfectNetwork(),
    PingSimulationOptions.CreateStableNetwork(),
    PingSimulationOptions.CreateUnreliableNetwork(),
    new PingSimulationOptions { BasePacketLossRate = 1.0 } // Total failure
};
```

## Performance Characteristics

- **Memory**: Minimal - no packet buffers or network state
- **CPU**: Light - simple math operations
- **Latency**: Accurate simulation via `Task.Delay`
- **Throughput**: Limited only by `Task.Delay` granularity

## Limitations

1. **No actual network traffic** - Won't detect real network issues
2. **Simplified model** - Real networks are more complex
3. **Single-threaded Random** - Not thread-safe across multiple transceivers
4. **No correlation** - Each ping is independent

## Migration from Real to Simulated

Easy switching via factory interface:

```csharp
// Development/Testing
IPingTransceiverFactory factory = new PingTransceiverFactorySimulated(options);

// Production
IPingTransceiverFactory factory = new PingTransceiverFactoryIcmp();

// Same code works with both!
var transceiver = factory.Create(ipAddress, cancellationToken);
```