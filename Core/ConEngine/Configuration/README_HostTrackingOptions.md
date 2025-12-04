# Host Tracking Configuration - Options Pattern

## Overview

Host tracking configuration uses the **Options Pattern** with validation and dependency injection as recommended by .NET best practices. This ensures type-safe, validated configuration with null-safety guarantees.

## Configuration Structure

### HostTrackingOptions

Defined in `ConEngine.Configuration.HostTrackingOptions`:

```csharp
public class HostTrackingOptions
{
    public const string SectionName = "HostTracking";
    
    public int HistorySize { get; set; } = 100;
    public int MinUsableSample { get; set; } = 10;
}
```

### Configuration Parameters

| Parameter | Type | Default | Range | Description |
|-----------|------|---------|-------|-------------|
| `HistorySize` | `int` | 100 | 1-1000 | Number of recent ping results to keep in memory per host |
| `MinUsableSample` | `int` | 10 | 1 to HistorySize | Minimum samples required before statistics are considered reliable |

### Validation Rules

1. **HistorySize**
   - Must be between 1 and 1000
   - Controls memory usage per host tracker

2. **MinUsableSample**
   - Must be at least 1
   - Cannot exceed `HistorySize`
   - Determines when `IsUsableSample` property becomes `true`

## Configuration Files

### appsettings.json (Production)

```json
{
  "HostTracking": {
    "HistorySize": 100,
    "MinUsableSample": 10
  }
}
```

### appsettings.Development.json (Development)

```json
{
  "HostTracking": {
    "HistorySize": 20,
    "MinUsableSample": 5
  }
}
```

**Note:** Development uses smaller values for faster testing and reduced memory usage.

## Dependency Injection Setup

### ConTinker\Program.cs

```csharp
hostBuilder.ConfigureServices((context, services) =>
{
    // Configure options with validation
    services.AddOptions<HostTrackingOptions>()
        .Bind(context.Configuration.GetSection(HostTrackingOptions.SectionName))
        .ValidateOnStart();
    
    // Register validator
    services.AddSingleton<IValidateOptions<HostTrackingOptions>, HostTrackingOptionsValidator>();
    
    // Other services...
});

// Validate on startup
var options = host.Services.GetRequiredService<IOptions<HostTrackingOptions>>().Value;
```

### Validation Behavior

- **ValidateOnStart()** - Configuration is validated when the host starts
- **OptionsValidationException** - Thrown if validation fails
- **Fail-fast behavior** - Application won't start with invalid configuration

## Usage in Code

### Injecting Options

```csharp
public class WatcherEngine
{
    private readonly HostTrackingOptions _trackingOptions;
    
    public WatcherEngine(
        TimeProvider timeProvider,
        IPingTransceiverFactory pingFactory,
        ILoggerFactory loggerFactory,
        IOptions<HostTrackingOptions> trackingOptions)
    {
        _trackingOptions = trackingOptions.Value;
    }
}
```

### Creating HostTracker with Options

```csharp
// Automatically uses configured values
var tracker = new HostTracker(ipAddress, _trackingOptions);
```

### Accessing Configuration Values

```csharp
int historySize = _trackingOptions.HistorySize;
int minSample = _trackingOptions.MinUsableSample;
```

## IsUsableSample Property

The `HostTracker.IsUsableSample` property indicates whether enough samples have been collected for reliable statistics.

```csharp
var tracker = engine.GetTracker(ipAddress);

if (tracker.IsUsableSample)
{
    // Statistics are reliable
    Console.WriteLine($"Success rate: {tracker.SuccessRate:F1}%");
    Console.WriteLine($"Average RTT: {tracker.AverageRoundtripTime:F1}ms");
}
else
{
    // Still collecting samples
    int needed = tracker.MinUsableSample - tracker.HistoryCount;
    Console.WriteLine($"Building sample... need {needed} more pings");
}
```

### Use Cases for IsUsableSample

1. **UI Display** - Show "Collecting data..." message when `!IsUsableSample`
2. **Alerting** - Don't trigger alerts until sample is usable
3. **Statistics** - Display warnings when sample size is insufficient
4. **Reporting** - Filter out hosts without usable samples

## Configuration Validation

### Startup Validation

```bash
# Valid configuration - starts normally
$ dotnet run
[12:00:00 INF] Starting with HostTracking configuration: HistorySize=100, MinUsableSample=10

# Invalid configuration - fails on startup
$ dotnet run
[12:00:00 FTL] Configuration validation failed: MinUsableSample (15) cannot be greater than HistorySize (10).
```

### Validation Messages

| Error | Message |
|-------|---------|
| HistorySize out of range | `HistorySize must be between 1 and 1000, but was {value}.` |
| MinUsableSample < 1 | `MinUsableSample must be at least 1, but was {value}.` |
| MinUsableSample > HistorySize | `MinUsableSample ({min}) cannot be greater than HistorySize ({history}).` |

## Memory Considerations

### Per-Host Memory Usage

```
Memory per host = HistorySize × sizeof(PingHistoryEntry)
                ? HistorySize × 80 bytes
```

**Examples:**
- HistorySize = 10: ~800 bytes per host
- HistorySize = 100: ~8 KB per host
- HistorySize = 1000: ~80 KB per host

### Total Memory for Multiple Hosts

```
Total = Number of hosts × Memory per host
```

**Examples:**
- 100 hosts × 100 history = ~800 KB
- 1000 hosts × 100 history = ~8 MB
- 100 hosts × 1000 history = ~8 MB

## Environment-Specific Configuration

### Production

```json
{
  "HostTracking": {
    "HistorySize": 100,
    "MinUsableSample": 10
  }
}
```

- Balanced between memory usage and statistics quality
- Requires 10 samples before statistics are considered reliable

### Development

```json
{
  "HostTracking": {
    "HistorySize": 20,
    "MinUsableSample": 5
  }
}
```

- Smaller values for faster testing
- Quicker feedback during development
- Lower memory footprint

### Testing

```json
{
  "HostTracking": {
    "HistorySize": 5,
    "MinUsableSample": 3
  }
}
```

- Minimal values for unit tests
- Fast test execution
- Predictable behavior

## Best Practices

### 1. Environment-Specific Settings

Use different values per environment based on requirements:
- **Production**: Higher history size for better statistics
- **Development**: Lower values for faster iteration
- **Testing**: Minimal values for predictable tests

### 2. MinUsableSample Guidelines

Recommended ratios:
- **Conservative**: MinUsableSample = HistorySize (100% of window)
- **Balanced**: MinUsableSample = HistorySize / 10 (10% of window)
- **Aggressive**: MinUsableSample = 3-5 (absolute minimum)

### 3. Memory Management

Consider your deployment:
- **Cloud/Container**: Smaller history sizes
- **Dedicated Server**: Larger history sizes acceptable
- **Many Hosts**: Reduce per-host memory

### 4. Validation

Always validate configuration on startup:
```csharp
services.AddOptions<HostTrackingOptions>()
    .Bind(configuration)
    .ValidateOnStart();  // ? Important!
```

## Troubleshooting

### Configuration Not Loading

1. **Check file location** - Must be in output directory
2. **Check project file** - Ensure `CopyToOutputDirectory` is set
3. **Check JSON syntax** - Use JSON validator
4. **Check section name** - Must match `HostTrackingOptions.SectionName`

### Validation Errors

1. **Check value ranges** - See validation rules above
2. **Check environment** - Verify correct appsettings.{Environment}.json
3. **Check logs** - Validation exceptions are logged on startup

### Runtime Issues

1. **Insufficient samples** - Check `IsUsableSample` before using statistics
2. **Memory usage** - Reduce HistorySize if needed
3. **Performance** - Smaller history = faster statistics calculation

## Migration Guide

### From Hard-Coded Values

```csharp
// Before
var tracker = new HostTracker(ipAddress, 10);

// After
var tracker = new HostTracker(ipAddress, _trackingOptions);
```

### From Custom Configuration

```csharp
// Before - custom config
var historySize = int.Parse(ConfigurationManager.AppSettings["HistorySize"]);
var tracker = new HostTracker(ipAddress, historySize);

// After - options pattern
services.AddOptions<HostTrackingOptions>()
    .Bind(context.Configuration.GetSection("HostTracking"));
var tracker = new HostTracker(ipAddress, _trackingOptions);
```

## Testing

### Unit Tests with Options

```csharp
[Fact]
public void HostTracker_UsesConfiguredHistorySize()
{
    var options = new HostTrackingOptions
    {
        HistorySize = 50,
        MinUsableSample = 10
    };
    
    var tracker = new HostTracker(IPAddress.Parse("8.8.8.8"), options);
    
    Assert.Equal(50, tracker.HistoryCapacity);
    Assert.Equal(10, tracker.MinUsableSample);
}
```

### Integration Tests

```csharp
[Fact]
public void Configuration_ValidatesOnStartup()
{
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string>
        {
            ["HostTracking:HistorySize"] = "10",
            ["HostTracking:MinUsableSample"] = "15"  // Invalid!
        })
        .Build();
    
    var services = new ServiceCollection();
    services.AddOptions<HostTrackingOptions>()
        .Bind(config.GetSection("HostTracking"))
        .ValidateOnStart();
    
    var provider = services.BuildServiceProvider();
    
    Assert.Throws<OptionsValidationException>(() =>
        provider.GetRequiredService<IOptions<HostTrackingOptions>>().Value);
}
```