using ConCore;

namespace ConEngine.Tracking;

/// <summary>A single ping result as kept in a host's history window.</summary>
public record PingHistoryEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public bool Success { get; init; }
    /// <summary>Round-trip time in milliseconds.</summary>
    public long RoundtripTime { get; init; }
    public string? Message { get; init; }

    public static PingHistoryEntry FromPingResult(PingResult result) => new()
    {
        Timestamp = result.Timestamp,
        Success = result.Success,
        RoundtripTime = result.RoundtripTime,
        Message = result.Message,
    };

    public override string ToString()
    {
        var time = Success ? $"{RoundtripTime} ms" : Message ?? "Failed";
        return $"{Timestamp:HH:mm:ss.fff} {time}";
    }
}
