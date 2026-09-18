using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ConCore;

namespace ConEngine.Tests.Fakes;

/// <summary>
/// Scriptable resolver: answers per hostname can be changed while an engine
/// runs, and every lookup is counted. Unknown names throw like a real
/// "host not found".
/// </summary>
public sealed class FakeDomainNameResolverFactory : IDomainNameResolverFactory
{
    public ConcurrentDictionary<string, IPAddress[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> ResolveCount { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Names that resolve to a FAILED result (not an exception) with this status text.</summary>
    public ConcurrentDictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IDomainNameResolver Create(CancellationToken cancellationToken) => new Resolver(this);

    private sealed class Resolver(FakeDomainNameResolverFactory owner) : IDomainNameResolver
    {
        public Task<DomainResolutionResult> ResolveAsync(string domainName, int timeoutMs)
        {
            owner.ResolveCount.AddOrUpdate(domainName, 1, (_, n) => n + 1);
            if (owner.Failures.TryGetValue(domainName, out var status))
                return Task.FromResult(new DomainResolutionResult(domainName, success: false,
                    Array.Empty<IPAddress>(), status));
            if (!owner.Answers.TryGetValue(domainName, out var ips))
                throw new SocketException(11001); // WSAHOST_NOT_FOUND
            return Task.FromResult(new DomainResolutionResult(domainName, success: true, ips));
        }

        public void Dispose() { }
    }
}
