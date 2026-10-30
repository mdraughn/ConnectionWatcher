namespace ConCore.Targets;

/// <summary>
/// A named, reusable bundle of ping-discipline and analysis settings.
/// </summary>
public record PingProfile
{
    public required string Name { get; init; }
    public TimeSpan NormalInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan BurstInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BurstMaxDuration { get; init; } = TimeSpan.FromSeconds(60);
    public int HistorySize { get; init; } = 100;
    public int MinUsableSample { get; init; } = 10;
    public double RespondingThreshold { get; init; } = 0.95;
    public double FalteringThreshold { get; init; } = 0.75;
    public double DownThreshold { get; init; } = 0.10;
    public double RecoveringThreshold { get; init; } = 0.25;

    /// <summary>Preset for LAN targets: fast steady pinging is fine.</summary>
    public static PingProfile Internal { get; } = new() { Name = "Internal", NormalInterval = TimeSpan.FromSeconds(1) };

    /// <summary>Preset for hops on the route to the internet that the user does not
    /// own but may probe steadily — the ISP gateway, the modem.</summary>
    public static PingProfile Path { get; } = new() { Name = "Path", NormalInterval = TimeSpan.FromSeconds(5) };

    /// <summary>Preset for public internet sites: polite steady cadence.</summary>
    public static PingProfile Public { get; } = new() { Name = "Public", NormalInterval = TimeSpan.FromSeconds(10) };

    /// <summary>Preset for LAN devices tracked beside the verdict (cameras, IoT, NAS):
    /// a smaller, more forgiving window so a sleepy device is not "down" for one missed ping.
    /// Same interval as <see cref="Public"/> so the engine's stall window is unchanged.</summary>
    public static PingProfile Local { get; } = new()
    {
        Name = "Local",
        NormalInterval = TimeSpan.FromSeconds(10),
        HistorySize = 30,
        MinUsableSample = 6,
        RespondingThreshold = 0.80,
        FalteringThreshold = 0.50,
    };

    /// <summary>Preset for DNS resolvers (spec 2026-10-10 §4.1): one query every
    /// half minute, a 2 s burst for a minute on the first miss, the Local-style
    /// small forgiving window. A slow query is not a Faltering; a dead resolver
    /// is Down about a minute after its first miss.</summary>
    public static PingProfile Resolver { get; } = new()
    {
        Name = "Resolver",
        NormalInterval = TimeSpan.FromSeconds(30),
        PingTimeout = TimeSpan.FromSeconds(5),
        BurstInterval = TimeSpan.FromSeconds(2),
        HistorySize = 30,
        MinUsableSample = 6,
        RespondingThreshold = 0.80,
        FalteringThreshold = 0.50,
    };

    /// <summary>The presets every configuration has, in display order.</summary>
    public static IReadOnlyList<PingProfile> BuiltIns { get; } = [Internal, Path, Public, Local, Resolver];

    /// <summary>True for a built-in preset name, ignoring case.</summary>
    public static bool IsBuiltIn(string name) =>
        BuiltIns.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
