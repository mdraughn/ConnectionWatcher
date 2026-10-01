using System.Net;

namespace ConInfrastructure.Asn;

/// <summary>The autonomous system an address belongs to: number, registry name, country.</summary>
public record AsnInfo(int Asn, string Name, string? CountryCode);

/// <summary>
/// IP → autonomous system. Implementations return null rather than throw:
/// for a private/carrier address, a resolver that does not answer in time,
/// or an answer that does not parse. Results may be cached.
/// </summary>
public interface IAsnLookup
{
    Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct);
}
