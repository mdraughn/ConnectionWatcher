namespace ConEngine.Tracking;

/// <summary>Named groups of <see cref="HostState"/>, so call sites say what they mean
/// instead of restating the set.</summary>
public static class HostStateExtensions
{
    /// <summary>The state machine saw replies in its window: Responding, Faltering or Recovering.</summary>
    public static bool IsAnswering(this HostState state) =>
        state is HostState.Responding or HostState.Faltering or HostState.Recovering;

    /// <summary>No verdict on this target yet: Initializing or Starting.</summary>
    public static bool IsUndetermined(this HostState state) =>
        state is HostState.Initializing or HostState.Starting;

    /// <summary>Down or Unreachable.</summary>
    public static bool IsFailed(this HostState state) =>
        state is HostState.Down or HostState.Unreachable;

    /// <summary>No trouble seen: Responding, or Starting before any verdict.</summary>
    public static bool IsHealthy(this HostState state) =>
        state is HostState.Responding or HostState.Starting;
}
