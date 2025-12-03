# Logger Architecture Fix

## Problem

The logging types were mismatched throughout the component hierarchy:
- `TinkerService` had `ILogger<TinkerService>`
- `WatcherEngine` expected `ILogger<WatcherEngine>`
- `PingManager` used non-generic `ILogger`
- `PingController` used non-generic `ILogger`

This caused type mismatches when passing loggers down the component hierarchy.

## Solution

Implemented a proper logging architecture using `ILoggerFactory` to create appropriately-typed loggers at each level.

### Architecture Overview

```
TinkerService (receives ILoggerFactory + ILogger<TinkerService>)
    ? creates logger for
WatcherEngine (receives ILoggerFactory + creates ILogger<WatcherEngine>)
    ? creates logger for
PingManager (receives ILoggerFactory + creates ILogger<PingManager>)
    ? creates logger for
PingController (receives ILogger with IP-specific category)
```

### Key Changes

#### 1. TinkerService
```csharp
public TinkerService(
    ILogger<TinkerService> log,         // For TinkerService-specific logs
    TimeProvider timeProvider,
    IPingTransceiverFactory pingFactory,
    ILoggerFactory loggerFactory)       // NEW: To create loggers for children
{
    _loggerFactory = loggerFactory;
    // Creates WatcherEngine with factory
    _watcherEngine = new WatcherEngine(_timeProvider, _pingFactory, _loggerFactory);
}
```

#### 2. WatcherEngine
```csharp
public WatcherEngine(
    TimeProvider timeProvider,
    IPingTransceiverFactory pingFactory,
    ILoggerFactory loggerFactory)       // NEW: Receives factory
{
    _loggerFactory = loggerFactory;
    _log = _loggerFactory.CreateLogger<WatcherEngine>();  // Creates own logger
    
    // Later creates PingManager with factory
    var pingManagerLogger = _loggerFactory.CreateLogger<PingManager>();
    _manager = new PingManager(timeProvider, pingFactory, cancellationToken, pingManagerLogger);
}
```

#### 3. PingManager
```csharp
// Constructor overload for ILoggerFactory
public PingManager(
    TimeProvider timeProvider,
    IPingTransceiverFactory pingTransceiverFactory,
    CancellationToken globalCancellationToken,
    ILoggerFactory loggerFactory)       // NEW: Factory overload
{
    _loggerFactory = loggerFactory;
    _log = _loggerFactory.CreateLogger<PingManager>();
    
    // Creates IP-specific logger for each controller
    var controllerLogger = _loggerFactory.CreateLogger($"ConEngine.Network.PingController[{ipAddress}]");
    controller = new PingController(_timeProvider, _pingTransceiverFactory, controllerLogger);
}

// Backward compatible constructor for direct ILogger
public PingManager(
    TimeProvider timeProvider,
    IPingTransceiverFactory pingTransceiverFactory,
    CancellationToken globalCancellationToken,
    ILogger<PingManager> log)           // Keeps existing signature
```

#### 4. PingController
```csharp
// Already using non-generic ILogger - no changes needed
public PingController(
    TimeProvider timeProvider,
    IPingTransceiverFactory pingTransceiverFactory,
    ILogger log)                        // Non-generic ILogger
```

## Benefits

### 1. Proper Log Categorization
Each component has its own logger category:
- `ConTinker.TinkerService`
- `ConEngine.WatcherEngine`
- `ConEngine.Network.PingManager`
- `ConEngine.Network.PingController[192.168.99.1]` (IP-specific)

### 2. Flexible Filtering
Log configuration can target specific components:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "ConEngine.Network.PingController": "Trace",
      "ConEngine.Network.PingManager": "Debug"
    }
  }
}
```

### 3. Clear Log Context
Log entries clearly show their source:
```
[15:30:45 INF] TinkerService starting... (ConTinker.TinkerService)
[15:30:45 INF] WatcherEngine starting... (ConEngine.WatcherEngine)
[15:30:45 DBG] Starting manager... (ConEngine.WatcherEngine)
[15:30:45 INF] Created PingController for 8.8.8.8 (ConEngine.Network.PingManager)
[15:30:45 TRC] Pinging 8.8.8.8 ... (ConEngine.Network.PingController[8.8.8.8])
```

### 4. Backward Compatibility
Existing code using `ILogger<T>` directly still works:
```csharp
// Still works!
var manager = new PingManager(timeProvider, factory, token, logger);
```

### 5. Testability
Each component can be tested with mock loggers:
```csharp
var mockLoggerFactory = new Mock<ILoggerFactory>();
var mockLogger = new Mock<ILogger<WatcherEngine>>();
mockLoggerFactory.Setup(f => f.CreateLogger<WatcherEngine>()).Returns(mockLogger.Object);

var engine = new WatcherEngine(timeProvider, factory, mockLoggerFactory.Object);
```

## Design Pattern

This implements the **Logger Factory Pattern**:

1. **Top-level component** receives `ILoggerFactory` from DI
2. **Each component**:
   - Creates its own typed logger from the factory
   - Passes the factory down to child components
   - Child components create their own loggers

3. **Benefits**:
   - Proper separation of concerns
   - Each component logs with appropriate category
   - No type mismatches
   - Full IntelliSense support
   - Performance (loggers are cached by factory)

## Migration Guide

### For New Components
```csharp
public class MyComponent
{
    private readonly ILogger<MyComponent> _log;
    private readonly ILoggerFactory _loggerFactory;
    
    public MyComponent(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<MyComponent>();
    }
    
    public void CreateChild()
    {
        var childLogger = _loggerFactory.CreateLogger<ChildComponent>();
        var child = new ChildComponent(childLogger, _loggerFactory);
    }
}
```

### For Existing Components
Add `ILoggerFactory` parameter while keeping existing constructor:
```csharp
// Old constructor - keep for compatibility
public MyComponent(ILogger<MyComponent> logger)
    : this(logger, new LoggerFactory())
{ }

// New constructor - uses factory
public MyComponent(ILoggerFactory loggerFactory)
    : this(loggerFactory.CreateLogger<MyComponent>(), loggerFactory)
{ }

// Private constructor - actual implementation
private MyComponent(ILogger<MyComponent> logger, ILoggerFactory loggerFactory)
{
    _log = logger;
    _loggerFactory = loggerFactory;
}
```

## Testing

Verify proper categorization with logging configuration:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "ConEngine": "Debug",
      "ConEngine.Network.PingController": "Trace"
    }
  }
}
```

This will show:
- Only warnings from other components
- Debug level from all ConEngine components
- Trace level from PingControllers specifically