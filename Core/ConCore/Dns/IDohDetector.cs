namespace ConCore.Dns;

/// <summary>Which of the host's resolvers the OS reaches over DNS-over-HTTPS, so the
/// port-53 check can say it may not reflect what the OS really uses (spec 2026-10-10 §4.5).
/// The interface lives in core so ConfigHealth (persistence) can consume it; the
/// Windows reader lives in infrastructure and the host wires it in.</summary>
public interface IDohDetector
{
    IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses);
}

public sealed class NullDohDetector : IDohDetector
{
    public IReadOnlyList<string> AddressesUsingDoh(IEnumerable<string> resolverAddresses) => [];
}
