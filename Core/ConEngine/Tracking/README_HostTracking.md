# Host Tracking System

A comprehensive tracking system for monitoring ping history and statistics across multiple hosts using efficient sliding window data structures.

## Overview

The host tracking system provides:
- **Historic ping data** with configurable sliding window (default: 10 most recent results)
- **Real-time statistics** including success rates, latency metrics, and consecutive result counts
- **Efficient storage** using circular buffer for O(1) operations
- **Thread-safe** concurrent access across multiple ping operations
- **Future-ready** design that doesn't assume IP-based tracking (prepared for hostname tracking)

## Architecture

### Components

```
WatcherEngine
    ??> ConcurrentDictionary<IPAddress, HostTracker>
            ??> CircularBuffer<PingHistoryEntry> (sliding window)
```

### Key Classes

#### 1. `PingHistoryEntry` (Record)
Immutable snapshot of a single ping result.

```csharp
public record PingHistoryEntry
{
    DateTimeOffset Timestamp
    bool Success
    long RoundtripTime
    string? Message
}
```

#### 2. `CircularBuffer<T>` (Generic Data Structure)
Fixed-size circular buffer for efficient sliding window.

**Features:**
- O(1) add, get latest, get oldest
- O(n) iteration in chronological order
- Thread-safe with internal locking
- Automatically overwrites oldest when full

**Operations:**
```csharp
var buffer = new CircularBuffer<T>(capacity: 10);
buffer.Add(item);                    // O(1)
T latest = buffer.GetLatest();       // O(1)
T oldest = buffer.GetOldest();       // O(1)
T[] all = buffer.ToArray();          // O(n)
T[] reversed = buffer.ToArrayReversed(); // O(n)
```

#### 3. `HostTracker`
Tracks complete ping history and statistics for a single host.

**Properties:**
- `HostAddress` - The IP address being tracked
- `TotalPingCount` - Total pings sent (all time)
- `SuccessfulPingCount` / `FailedPingCount` - Success/failure counts
- `LatestResult` - Most recent ping entry
- `LatestSuccess` / `LatestRoundtripTime` / `LatestMessage` - Latest ping details
- `SuccessRate` - Overall success rate (0-100%)
- `RecentSuccessRate` - Success rate in current window (0-100%)
- `AverageRoundtripTime` - Average RTT for successful pings in window
- `MinimumRoundtripTime` / `MaximumRoundtripTime` - RTT range in window
- `ConsecutiveSuccessCount` / `ConsecutiveFailureCount` - Current streak

**Methods:**
```csharp
void RecordPingResult(PingResult result)
PingHistoryEntry[] GetHistory()              // Chronological order
PingHistoryEntry[] GetHistoryReversed()      // Reverse chronological
HostTrackerSummary GetSummary()              // Thread-safe snapshot
void ClearHistory()                          // Clear window, keep stats
```

#### 4. `WatcherEngine` (Updated)
Central engine now maintains tracking dictionary.

**New Properties:**
- `TrackedHostCount` - Number of hosts being tracked
- `GetAllTrackers()` - All host trackers
- `GetTracker(IPAddress)` - Specific host tracker
- `GetAllTrackerSummaries()` - Thread-safe snapshots of all hosts

**New Methods:**
```csharp
void StartWatching(IPAddress)     // Begin tracking + pinging
void StopWatching(IPAddress)      // Stop pinging, keep tracking
bool RemoveTracking(IPAddress)    // Remove tracking data
void ClearAllTracking()           // Clear all tracking data
```

## Usage Examples

### Basic Tracking

```csharp
var engine = new WatcherEngine(timeProvider, factory, loggerFactory);
await engine.StartAsync(cancellationToken);

// Start watching hosts
engine.StartWatching(IPAddress.Parse("8.8.8.8"));
engine.StartWatching(IPAddress.Parse("1.1.1.1"));

// Let it run...
await Task.Delay(TimeSpan.FromSeconds(30));

// View statistics
foreach (var summary in engine.GetAllTrackerSummaries())
{
    Console.WriteLine($"{summary.HostAddress}: {summary.SuccessRate:F1}% success");
    Console.WriteLine($"  Average RTT: {summary.AverageRoundtripTime:F1}ms");
    Console.WriteLine($"  Recent: {summary.RecentSuccessRate:F1}%");
}
```

### Detailed History Access

```csharp
var tracker = engine.GetTracker(IPAddress.Parse("8.8.8.8"));
if (tracker != null)
{
    // Get latest result
    var latest = tracker.LatestResult;
    Console.WriteLine($"Latest: {(latest.Success ? "?" : "?")} {latest.RoundtripTime}ms");

    // Get full history
    var history = tracker.GetHistoryReversed(); // Newest first
    foreach (var entry in history)
    {
        Console.WriteLine($"{entry.Timestamp:HH:mm:ss} - {entry}");
    }

    // Check consecutive results
    if (tracker.ConsecutiveFailureCount >= 3)
    {
        Console.WriteLine($"?? {tracker.ConsecutiveFailureCount} consecutive failures!");
    }
}
```

### Real-Time Monitoring

```csharp
// Update display every 2 seconds
while (!cancellationToken.IsCancellationRequested)
{
    var tracker = engine.GetTracker(monitoredHost);
    if (tracker != null)
    {
        Console.Clear();
        Console.WriteLine($"Host: {tracker.HostAddress}");
        Console.WriteLine($"Success Rate: {tracker.SuccessRate:F1}%");
        Console.WriteLine($"Average RTT: {tracker.AverageRoundtripTime:F1}ms");
        Console.WriteLine($"Consecutive: {tracker.ConsecutiveSuccessCount} successes");
        
        // Show recent history
        foreach (var entry in tracker.GetHistoryReversed().Take(5))
        {
            Console.WriteLine($"  {entry}");
        }
    }
    
    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
}
```

### Tracking Management

```csharp
// Start watching
engine.StartWatching(host);

// Stop watching but keep history
engine.StopWatching(host);

// Can still access tracker
var tracker = engine.GetTracker(host);
Console.WriteLine($"Historical data: {tracker.TotalPingCount} pings");

// Remove tracking data
engine.RemoveTracking(host); // Tracker is now gone

// Clear all tracking
engine.ClearAllTracking();
```

## Design Decisions

### 1. IP Address as Key (Future-Proof Design)
Currently uses `IPAddress` as dictionary key, but:
- Class names don't mention "IP" (e.g., `HostTracker` not `IPTracker`)
- Prepared for future enhancement to track by hostname
- Can add hostname-to-IP mapping layer later

### 2. Circular Buffer for Sliding Window
Benefits:
- **Memory efficient** - Fixed size, no allocations after initialization
- **O(1) operations** - Add, get latest, get oldest all constant time
- **Thread-safe** - Internal locking for concurrent access
- **Natural semantics** - Automatically maintains "last N" items

Alternatives considered:
- `Queue<T>` - Would require dequeue on add (O(n) worst case)
- `LinkedList<T>` - More memory overhead per node
- `List<T>` - Would need manual capacity management

### 3. ConcurrentDictionary for Host Tracking
- Thread-safe for concurrent ping operations
- No manual locking needed at dictionary level
- Efficient GetOrAdd for lazy initialization

### 4. Separate Statistics and History
- **All-time stats** (`TotalPingCount`, `SuccessfulPingCount`) - Never reset
- **Window history** (`CircularBuffer`) - Last N results only
- Allows understanding both long-term and recent behavior

### 5. Immutable Records for Thread Safety
- `PingHistoryEntry` is immutable record
- `HostTrackerSummary` is immutable record
- Safe to pass between threads, no defensive copying needed

## Performance Characteristics

### Memory Usage
- **Per host**: ~1KB (10 entries × 100 bytes/entry + overhead)
- **100 hosts**: ~100KB
- **1000 hosts**: ~1MB

### Operation Complexity
| Operation | Complexity | Notes |
|-----------|-----------|-------|
| Record ping | O(1) | Add to circular buffer |
| Get latest | O(1) | Direct buffer access |
| Get history | O(n) | n = window size (10) |
| Calculate stats | O(n) | Over window only |
| Get tracker | O(1) | Dictionary lookup |
| All summaries | O(m) | m = number of hosts |

### Threading
- **Multiple ping threads** ? Write to different trackers: No contention
- **Single tracker** ? Multiple pings: Minimal lock contention
- **Summary snapshots** ? Thread-safe, no blocking

## Configuration Options

### History Size
```csharp
// Default: 10 entries
var tracker = new HostTracker(ipAddress);

// Custom size
var tracker = new HostTracker(ipAddress, historySize: 100);
```

### Statistics Calculation
All statistics are calculated on-demand from current window:
- No pre-calculation overhead
- Always reflects current data
- Minimal memory impact

## Extension Points

### 1. Add Hostname Tracking
```csharp
// Future: Track by hostname instead of/in addition to IP
public class HostTracker
{
    public IPAddress HostAddress { get; }
    public string? Hostname { get; set; }  // Future: Add hostname
}
```

### 2. Custom History Entry Types
```csharp
// Inherit to add custom data
public record ExtendedPingHistoryEntry : PingHistoryEntry
{
    public string? Region { get; init; }
    public string? ServerName { get; init; }
}
```

### 3. Persistence
```csharp
// Export tracking data
public class HostTrackerPersistence
{
    public static void Export(HostTracker tracker, string path)
    {
        var data = tracker.GetHistory();
        File.WriteAllText(path, JsonSerializer.Serialize(data));
    }
    
    public static HostTracker Import(string path)
    {
        // Reconstruct from saved data
    }
}
```

### 4. Alerting
```csharp
// Example: Alert on consecutive failures
private void HandlePingResult(PingResult message)
{
    var tracker = _hostTrackers.GetOrAdd(message.IpAddress, ...);
    tracker.RecordPingResult(message);
    
    // Alert logic
    if (tracker.ConsecutiveFailureCount >= 5)
    {
        SendAlert($"Host {message.IpAddress} has {tracker.ConsecutiveFailureCount} consecutive failures!");
    }
}
```

## Best Practices

### 1. Use Summaries for Thread Safety
```csharp
// ? Don't hold reference across async operations
var tracker = engine.GetTracker(host);
await Task.Delay(1000);
var rate = tracker.SuccessRate; // May change during delay

// ? Use snapshot
var summary = engine.GetTracker(host)?.GetSummary();
await Task.Delay(1000);
var rate = summary.SuccessRate; // Immutable snapshot
```

### 2. Clean Up When Done
```csharp
// Stop pinging but keep data
engine.StopWatching(host);

// Later, if done with host
engine.RemoveTracking(host);
```

### 3. Monitor for Trends
```csharp
var tracker = engine.GetTracker(host);
if (tracker != null)
{
    // Compare recent vs overall
    if (tracker.RecentSuccessRate < tracker.SuccessRate * 0.5)
    {
        // Recent performance is less than 50% of overall - degradation!
    }
}
```

### 4. Use Appropriate History Size
```csharp
// Short-term monitoring: Small window
new HostTracker(host, 5);

// Long-term trends: Large window
new HostTracker(host, 100);

// Balance memory vs insight
```

## Testing

### Unit Testing HostTracker
```csharp
[Fact]
public void HostTracker_TracksSuccessRate()
{
    var tracker = new HostTracker(IPAddress.Parse("8.8.8.8"), historySize: 5);
    
    // Record 3 successes, 2 failures
    tracker.RecordPingResult(new PingResult(tracker.HostAddress, true, 10));
    tracker.RecordPingResult(new PingResult(tracker.HostAddress, true, 20));
    tracker.RecordPingResult(new PingResult(tracker.HostAddress, false, 0));
    tracker.RecordPingResult(new PingResult(tracker.HostAddress, true, 15));
    tracker.RecordPingResult(new PingResult(tracker.HostAddress, false, 0));
    
    Assert.Equal(60.0, tracker.SuccessRate, precision: 1);
    Assert.Equal(15.0, tracker.AverageRoundtripTime, precision: 1);
}
```

### Testing Circular Buffer
```csharp
[Fact]
public void CircularBuffer_OverwritesOldest()
{
    var buffer = new CircularBuffer<int>(3);
    
    buffer.Add(1);
    buffer.Add(2);
    buffer.Add(3);
    buffer.Add(4); // Overwrites 1
    
    var items = buffer.ToArray();
    Assert.Equal(new[] { 2, 3, 4 }, items);
}
```