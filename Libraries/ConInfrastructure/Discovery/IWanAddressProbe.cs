namespace ConInfrastructure.Discovery;

/// <summary>Result of asking the router for its public (WAN) address.</summary>
public record WanProbeResult(string? Address, string? FailureReason, bool PingOk, long? RttMs);

/// <summary>Discovers the router's WAN address (e.g. via UPnP-IGD).</summary>
public interface IWanAddressProbe
{
    Task<WanProbeResult> ProbeAsync(CancellationToken ct);
}
