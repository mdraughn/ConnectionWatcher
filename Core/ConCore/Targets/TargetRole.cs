namespace ConCore.Targets;

/// <summary>What a target gets for the role it plays when ConnectionWatcher creates or pre-fills it.</summary>
public sealed record TargetRole(string Profile, TargetScope Scope, bool Critical = false, string? Probe = null)
{
    /// <summary>The user's router: pinged fast, and the device you most want to hear about (spec §4.7).</summary>
    public static TargetRole Router { get; } = new(PingProfile.Internal.Name, TargetScope.Verdict, Critical: true);

    /// <summary>A hop on the route to the internet that the user does not own (ISP gateway, modem).</summary>
    public static TargetRole PathHop { get; } = new(PingProfile.Path.Name, TargetScope.Verdict);

    /// <summary>A public internet site that decides the verdict.</summary>
    public static TargetRole Anchor { get; } = new(PingProfile.Public.Name, TargetScope.Verdict);

    /// <summary>A LAN device tracked beside the verdict.</summary>
    public static TargetRole LocalDevice { get; } = new(PingProfile.Local.Name, TargetScope.Local);

    /// <summary>A DNS resolver, probed with DNS queries.</summary>
    public static TargetRole Resolver { get; } = new(PingProfile.Resolver.Name, TargetScope.Resolver, Probe: "dns");
}
