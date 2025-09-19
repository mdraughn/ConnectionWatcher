# Domain Name Resolution - ConEngine.Network

This document describes the domain name resolution functionality added to ConEngine.Network and ConInfrastructure, following the established patterns used by the ping functionality.

## Overview

The domain name resolution functionality provides a clean, async interface for resolving domain names to IP addresses while preserving the order returned by DNS and handling various error conditions gracefully.

## Components

### 1. `IDomainNameResolver` Interface (ConEngine.Network)

```csharp
public interface IDomainNameResolver : IDisposable
{
    string DomainName { get; }
    Task<DomainResolutionResult> ResolveAsync(int timeoutMs);
}
```

**Key Features:**
- Follows the same pattern as `IPingTarget`
- Implements `IDisposable` for proper resource management
- Accepts timeout parameter for each resolution operation
- Returns immutable result objects

### 2. `DomainResolutionResult` Record (ConEngine.Network)

```csharp
public record DomainResolutionResult
{
    public string DomainName { get; }
    public bool Success { get; }
    public IReadOnlyList<IPAddress> IpAddresses { get; }
    public string Status { get; }
    public long ResolutionTimeMs { get; }
    
    // Convenience properties
    public int AddressCount { get; }
    public IPAddress? FirstAddress { get; }
    public IEnumerable<IPAddress> IPv4Addresses { get; }
    public IEnumerable<IPAddress> IPv6Addresses { get; }
}
```

**Key Features:**
- **Immutable record** - thread-safe and predictable
- **Preserves order** - IP addresses are returned in the same order as DNS
- **Rich result data** - includes timing, status, and convenience accessors
- **Type safety** - uses .NET `IPAddress` classes
- **IPv4/IPv6 filtering** - convenience properties for filtering by address family

### 3. `DomainNameResolverImpl` Class (ConInfrastructure)

```csharp
public class DomainNameResolverImpl : IDomainNameResolver
{
    public DomainNameResolverImpl(string domainName, CancellationToken cancellationToken)
    public string DomainName { get; }
    public async Task<DomainResolutionResult> ResolveAsync(int timeoutMs)
}
```

**Key Features:**
- **High performance** - uses `System.Net.Dns.GetHostEntryAsync()`
- **Proper timeout handling** - combines timeout with cancellation tokens
- **Comprehensive error handling** - handles DNS errors, timeouts, and invalid domains
- **Resource management** - implements full `IDisposable` pattern
- **Detailed timing** - measures actual resolution time

## Usage Examples

### Basic Usage

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var resolver = new DomainNameResolverImpl("google.com", cts.Token);

var result = await resolver.ResolveAsync(timeoutMs: 5000);

if (result.Success)
{
    Console.WriteLine($"Resolved {result.DomainName} to {result.AddressCount} addresses:");
    foreach (var ip in result.IpAddresses)
    {
        Console.WriteLine($"  {ip} ({ip.AddressFamily})");
    }
}
else
{
    Console.WriteLine($"Failed to resolve {result.DomainName}: {result.Status}");
}
```

### Combined with Ping

```csharp
// First resolve the domain
using var resolver = new DomainNameResolverImpl("example.com", cancellationToken);
var resolutionResult = await resolver.ResolveAsync(5000);

if (resolutionResult.Success)
{
    // Then ping the first resolved IP
    using var pingTarget = new PingTargetIpImpl(resolutionResult.FirstAddress!, cancellationToken);
    var pingResult = await pingTarget.PingAsync(3000);
    
    Console.WriteLine($"Ping to {pingResult.IpAddress}: {pingResult}");
}
```

### Multiple Domains

```csharp
var domains = new[] { "google.com", "microsoft.com", "github.com" };
var tasks = domains.Select(async domain =>
{
    using var resolver = new DomainNameResolverImpl(domain, cancellationToken);
    return await resolver.ResolveAsync(5000);
});

var results = await Task.WhenAll(tasks);
foreach (var result in results)
{
    Console.WriteLine(result.ToDetailedString());
}
```

## Error Handling

The implementation handles various error conditions:

| Error Type | Status | Description |
|------------|--------|-------------|
| `HostNotFound` | "Host not found" | Domain doesn't exist |
| `TryAgain` | "Temporary DNS failure" | Temporary DNS server issue |
| `NoRecovery` | "Non-recoverable DNS error" | Permanent DNS error |
| `NoData` | "No DNS data found" | Domain exists but no IP records |
| `Timeout` | "Timeout" | Operation exceeded timeout |
| `Cancelled` | "Cancelled" | Operation was cancelled |
| `ArgumentException` | "Invalid domain name" | Malformed domain name |

## Performance Characteristics

- **Fast resolution** - Uses optimized `System.Net.Dns` APIs
- **Concurrent operations** - Multiple resolvers can run simultaneously
- **Memory efficient** - Minimal allocations, immutable results
- **Timeout control** - Precise timeout handling per operation

## Thread Safety

- **Resolver instances** - Not thread-safe (create separate instances per thread)
- **Result objects** - Immutable and fully thread-safe
- **Concurrent operations** - Multiple domains can be resolved concurrently

```csharp
// Concurrent resolution of multiple domains
var tasks = domains.Select(async domain =>
{
    using var resolver = new DomainNameResolverImpl(domain, cancellationToken);
    return await resolver.ResolveAsync(5000);
});

var results = await Task.WhenAll(tasks);
```

## Integration with Existing Code

The domain resolution functionality integrates seamlessly with the existing ping infrastructure:

1. **Resolve domain** ? Get IP addresses
2. **Create ping targets** ? Use resolved IPs
3. **Perform monitoring** ? Continuous ping operations

This enables hostname-based monitoring while maintaining the performance benefits of IP-based ping operations.