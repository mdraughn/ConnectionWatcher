using System.Net;

namespace ConInfrastructure.Asn;

/// <summary>Never looks anything up; for tests and for the lookup-off path.</summary>
public sealed class NullAsnLookup : IAsnLookup
{
    public static NullAsnLookup Instance { get; } = new();
    public Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct) => Task.FromResult<AsnInfo?>(null);
}
