using System.Text.Json.Serialization;

namespace ConCore.Targets;

/// <summary>
/// What a target is for. <see cref="Verdict"/> targets (the default) form the
/// reachability tree and feed the internet verdict. <see cref="Local"/>
/// targets are LAN devices: probed with history, shown in the LAN section,
/// and excluded from the verdict, suspicion, notifications and the ISP report.
/// <see cref="Resolver"/> targets are DNS resolvers (spec 2026-10-10): probed
/// with a DNS query, parentless, shown in the Name resolution section, and
/// excluded from the tree, loss windows, suspicion and notifications; the
/// DNS verdict rule (a later plan) is the only thing that reads them.
/// Serialized as a string everywhere ("Verdict" / "Local" / "Resolver").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetScope>))]
public enum TargetScope
{
    Verdict = 0,
    Local = 1,
    Resolver = 2,
}

public static class TargetScopeExtensions
{
    /// <summary>
    /// A member of the reachability tree: may have a parent, be a parent,
    /// be a pool parent or member. True only for <see cref="TargetScope.Verdict"/>.
    /// </summary>
    public static bool IsTree(this TargetScope scope) => scope == TargetScope.Verdict;
}
