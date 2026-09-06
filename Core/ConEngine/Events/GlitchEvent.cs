namespace ConEngine.Events;

public enum GlitchClass
{
    /// <summary>One target dropped a few pings and recovered.</summary>
    Target = 0,
    /// <summary>Multiple anchors glitched together — a brief internet-wide wobble.</summary>
    Internet = 1,
}

/// <summary>
/// A recorded sub-Faltering event: trouble too small to move the state
/// machine, but real. Episodes that produce any state transition are owned
/// by the transition ledger and never become glitches.
/// </summary>
public record GlitchEvent
{
    public required Guid TargetId { get; init; }
    public required string TargetName { get; init; }
    public GlitchClass Class { get; init; } = GlitchClass.Target;
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public int LostCount { get; init; }
    public int SampleCount { get; init; }
    public string? Detail { get; init; }

    /// <summary>True when this episode began within the coalesce gap of the
    /// previous glitch on the same target; the recorder extends that row.</summary>
    public bool Coalesced { get; init; }
}
