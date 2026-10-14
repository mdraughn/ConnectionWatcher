using System.Net;
using ConInfrastructure.NameResolution;

namespace ConWatch.Tests;

/// <summary>The OS resolver list, scripted. Empty unless a test says otherwise.</summary>
public sealed class FakeSystemResolverSource : ISystemResolverSource
{
    private readonly Lock _lock = new();
    private List<SystemResolver> _found = [];

    public void Set(params string[] addresses)
    {
        lock (_lock)
            _found = addresses.Select(a => new SystemResolver(IPAddress.Parse(a), a == "127.0.0.11")).ToList();
    }

    public IReadOnlyList<SystemResolver> Read()
    {
        lock (_lock)
            return _found.ToList();
    }
}
