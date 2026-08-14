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
    public DateTimeOffset TrackingStarted { get; init; }
    public int SampleSize { get; init; }
    public float SuccessRate { get; init; }
    public float FailureRate { get; init; }
    public float AverageRoundtripTime { get; init; }
    public DateTimeOffset Timestamp { get; set; }

    public override string ToString()
    {
        return $"{HostAddress}: {State}";
    }
}