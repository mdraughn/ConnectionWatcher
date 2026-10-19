using System.Net;

namespace ConEngine.Tracking;

public enum HostState
{
    Initializing,
    Starting,
    Responding,
    Faltering,
    Down,
    Recovering,
    Unreachable
}

/// <summary>
/// Represents a summary of host tracking statistics.
/// </summary>
public record HostStatus
{
    public required IPAddress HostAddress { get; init; }
    public HostState State { get; init; }
    /// <summary>True while the state is a warm-restart presumption that fresh
    /// samples have not yet confirmed.</summary>
    public bool Assumed { get; init; }
    public DateTimeOffset TrackingStarted { get; init; }
    public int SampleSize { get; init; }
    public double SuccessRate { get; init; }
    public double FailureRate { get; init; }
    public double AverageRoundtripTime { get; init; }
    public DateTimeOffset Timestamp { get; set; }

    public override string ToString()
    {
        return $"{HostAddress}: {State}";
    }
}