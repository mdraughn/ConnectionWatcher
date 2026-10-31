namespace ConEngine.Topology;

/// <summary>Overall internet health classification.</summary>
public enum VerdictState
{
    Unknown,
    Healthy,
    SiteIncident,
    InternetWeather,
    LinkDegraded,
    DnsDown,
    LinkDown
}

public enum DnsBlameKind { YourResolver, NoResolver }

/// <summary>Why name lookups fail while the internet is reachable (spec 2026-10-10 §4.4).</summary>
public sealed record DnsBlame(DnsBlameKind Kind, string Message, IReadOnlyList<string> Resolvers);
