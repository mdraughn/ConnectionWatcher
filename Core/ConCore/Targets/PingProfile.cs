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

    /// <summary>Preset for external targets: polite steady cadence.</summary>
    public static PingProfile External { get; } = new() { Name = "External", NormalInterval = TimeSpan.FromSeconds(10) };
}
