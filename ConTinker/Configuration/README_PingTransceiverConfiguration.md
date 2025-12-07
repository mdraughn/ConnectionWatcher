# Ping Transceiver Configuration

This document explains how to configure the ping transceiver behavior in the ConnectionWatcher application through the `appsettings.json` configuration file.

## Overview

The `PingTransceiver` configuration section allows you to choose between real ICMP pinging and simulated pinging, which is useful for development and testing without requiring actual network connectivity or elevated permissions.

## Configuration Section

Add the `PingTransceiver` section to your `appsettings.json` or `appsettings.Development.json` file:

```json
{
  "PingTransceiver": {
    "UseSimulation": false,
    "ShowControlWindow": true,
    "SimulationPreset": "Stable",
    "CustomSimulation": {
      // Optional: Only used when SimulationPreset is "Custom"
    }
  }
}
```

## Configuration Options

### UseSimulation

**Type:** `bool`  
**Default:** `false`  
**Description:** Determines whether to use simulated pinging (`true`) or real ICMP pinging (`false`).

- `false` - Uses real ICMP ping requests (requires appropriate network permissions)
- `true` - Uses simulated ping responses based on the configured simulation options

### ShowControlWindow

**Type:** `bool`  
**Default:** `true`  
**Description:** Controls whether to display the interactive WPF control window for simulations. Only applies when `UseSimulation` is `true`.

- `true` - Shows the interactive control window where you can manually trigger outages and view statistics
- `false` - Runs simulation in headless mode without UI

### SimulationPreset

**Type:** `string`  
**Default:** `"Stable"`  
**Allowed Values:** `"Perfect"`, `"Stable"`, `"Unreliable"`, `"Custom"`  
**Description:** Selects a predefined simulation profile. Only applies when `UseSimulation` is `true`.

#### Available Presets

##### Perfect
Simulates a perfect network with no packet loss and consistent low latency.
- Mean latency: 10ms (configurable)
- Standard deviation: 0.5ms
- Packet loss: 0%
- No outages

##### Stable
Simulates a typical stable network with occasional minor issues.
- Mean latency: 20ms
- Standard deviation: 5ms
- Packet loss: 0.1%
- Outage probability: 0.01% (very rare)
- Outage duration: ~5 seconds
- Packet loss during outage: 80%

##### Unreliable
Simulates an unreliable network with frequent issues.
- Mean latency: 100ms
- Standard deviation: 50ms
- Packet loss: 10%
- Outage probability: 1% per ping
- Outage duration: ~60 seconds
- Packet loss during outage: 100%

##### Custom
Uses the settings defined in the `CustomSimulation` section (see below).

### CustomSimulation

**Type:** `object`  
**Required when:** `SimulationPreset` is `"Custom"`  
**Description:** Detailed simulation parameters for custom network behavior.

#### Custom Simulation Properties

```json
{
  "CustomSimulation": {
    "MeanRoundTripTimeMs": 50.0,
    "RoundTripTimeStdDevMs": 10.0,
    "BasePacketLossRate": 0.05,
    "OutageProbability": 0.005,
    "MeanOutageDurationMs": 30000.0,
    "OutageDurationStdDevMs": 5000.0,
    "OutagePacketLossRate": 0.95,
    "MinRoundTripTimeMs": 1.0,
    "MaxRoundTripTimeMs": 500.0,
    "MinOutageDurationMs": 5000.0,
    "MaxOutageDurationMs": 120000.0,
    "RandomSeed": null
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MeanRoundTripTimeMs` | `double` | `20.0` | Average round-trip time in milliseconds |
| `RoundTripTimeStdDevMs` | `double` | `5.0` | Standard deviation for latency variation |
| `BasePacketLossRate` | `double` | `0.01` | Normal packet loss rate (0.0 to 1.0) |
| `OutageProbability` | `double` | `0.001` | Probability of outage per ping (0.0 to 1.0) |
| `MeanOutageDurationMs` | `double` | `10000.0` | Average outage duration in milliseconds |
| `OutageDurationStdDevMs` | `double` | `2000.0` | Standard deviation for outage duration |
| `OutagePacketLossRate` | `double` | `1.0` | Packet loss rate during outages (0.0 to 1.0) |
| `MinRoundTripTimeMs` | `double` | `1.0` | Minimum allowed round-trip time |
| `MaxRoundTripTimeMs` | `double` | `500.0` | Maximum allowed round-trip time |
| `MinOutageDurationMs` | `double` | `10000.0` | Minimum outage duration (10 seconds) |
| `MaxOutageDurationMs` | `double` | `300000.0` | Maximum outage duration (5 minutes) |
| `RandomSeed` | `int?` | `null` | Optional seed for reproducible simulations |

## Example Configurations

### Production Configuration (`appsettings.json`)

```json
{
  "PingTransceiver": {
    "UseSimulation": false,
    "ShowControlWindow": false,
    "SimulationPreset": "Stable"
  }
}
```

This configuration uses real ICMP pinging for production deployments.

### Development Configuration (`appsettings.Development.json`)

```json
{
  "PingTransceiver": {
    "UseSimulation": true,
    "ShowControlWindow": true,
    "SimulationPreset": "Unreliable",
    "CustomSimulation": {
      "MeanRoundTripTimeMs": 100.0,
      "RoundTripTimeStdDevMs": 50.0,
      "BasePacketLossRate": 0.1,
      "OutageProbability": 0.01,
      "MeanOutageDurationMs": 60000.0,
      "OutageDurationStdDevMs": 5000.0,
      "OutagePacketLossRate": 1.0,
      "MinRoundTripTimeMs": 1.0,
      "MaxRoundTripTimeMs": 500.0,
      "MinOutageDurationMs": 10000.0,
      "MaxOutageDurationMs": 300000.0,
      "RandomSeed": null
    }
  }
}
```

This configuration enables simulation with the interactive control window for development and testing.

### Headless Testing Configuration

```json
{
  "PingTransceiver": {
    "UseSimulation": true,
    "ShowControlWindow": false,
    "SimulationPreset": "Stable"
  }
}
```

This configuration enables simulation without the control window, useful for automated testing.

### Custom Reproducible Testing

```json
{
  "PingTransceiver": {
    "UseSimulation": true,
    "ShowControlWindow": false,
    "SimulationPreset": "Custom",
    "CustomSimulation": {
      "MeanRoundTripTimeMs": 25.0,
      "RoundTripTimeStdDevMs": 3.0,
      "BasePacketLossRate": 0.02,
      "OutageProbability": 0.001,
      "MeanOutageDurationMs": 15000.0,
      "OutageDurationStdDevMs": 3000.0,
      "OutagePacketLossRate": 0.9,
      "RandomSeed": 12345
    }
  }
}
```

This configuration provides reproducible test results using a fixed random seed.

## Validation

The configuration is validated at application startup. If validation fails, the application will log detailed error messages and terminate. Common validation errors include:

- Invalid `SimulationPreset` value (must be Perfect, Stable, Unreliable, or Custom)
- Missing `CustomSimulation` section when using Custom preset
- Invalid packet loss rates (must be between 0.0 and 1.0)
- Invalid probability values (must be between 0.0 and 1.0)
- Negative latency or duration values
- Min/Max values where max is less than min

## How It Works

The `ConfiguredPingTransceiverFactory` class reads the configuration at startup and creates the appropriate `IPingTransceiverFactory` implementation:

- When `UseSimulation` is `false`, it creates a `PingTransceiverFactoryIcmp` for real ICMP pinging
- When `UseSimulation` is `true`, it creates a `PingTransceiverInteractiveSimFactory` with the specified preset or custom settings

The factory is registered as a singleton in the dependency injection container and used throughout the application lifetime.

## Interactive Control Window

When `ShowControlWindow` is `true` and simulations are enabled, a WPF window appears showing:

- Real-time ping statistics for each monitored IP address
- Manual outage triggers for testing
- Visual indicators of current network state
- Configuration details for each simulator

This window runs on a separate UI thread and doesn't block the main application.

## Environment-Specific Configuration

Use standard ASP.NET Core configuration layering to override settings per environment:

1. `appsettings.json` - Default/production settings (UseSimulation = false)
2. `appsettings.Development.json` - Development overrides (UseSimulation = true)
3. `appsettings.Testing.json` - Testing overrides (if needed)
4. Environment variables - Runtime overrides
5. Command-line arguments - Runtime overrides

The application will automatically load the correct configuration based on the `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT` environment variable.

## See Also

- [Interactive Simulation README](../ConTestingInfrastructure/README_InteractiveSimulation.md) - Details on the simulation system
- [PingSimulationOptions](../ConTestingInfrastructure/PingSimulationOptions.cs) - Simulation option properties
- [ConfiguredPingTransceiverFactory](Configuration/ConfiguredPingTransceiverFactory.cs) - Factory implementation
