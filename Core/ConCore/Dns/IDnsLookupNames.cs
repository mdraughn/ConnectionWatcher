using System.Net;

namespace ConCore.Dns;

/// <summary>What a DNS probe asks for: the next lookup name for a given resolver.</summary>
public interface IDnsLookupNames
{
    /// <summary>The next name to ask <paramref name="resolver"/>, round-robin per
    /// resolver so one dead zone cannot fail a resolver on its own; null when
    /// there are no names.</summary>
    DnsLookupName? Next(IPAddress resolver);
}
