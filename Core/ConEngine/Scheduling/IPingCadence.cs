namespace ConEngine.Scheduling;

/// <summary>
/// Decides when the next ping happens. A PingController awaits this between pings.
/// </summary>
public interface IPingCadence
{
    /// <summary>Completes when it is time for the next ping. Waits only until cancellation; targets are never parked.</summary>
    Task WaitForNextPingAsync(CancellationToken ct);
}
