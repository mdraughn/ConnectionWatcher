namespace ConEngine.Scheduling;

/// <summary>
/// Decides when the next ping happens. A PingController awaits this between pings.
/// </summary>
public interface IPingCadence
{
    /// <summary>Completes when it is time for the next ping. May park indefinitely.</summary>
    Task WaitForNextPingAsync(CancellationToken ct);
}
