using ConEngine;

namespace ConWatch;

/// <summary>
/// One place that turns engine state into a health verdict. A monitor whose
/// job is to notice silence must notice its own: an engine with active
/// monitors that has delivered no result for longer than its stall window
/// is "EngineStalled", not "Healthy".
/// </summary>
public static class EngineHealth
{
    public sealed record Verdict(string Status, bool Stalled, TimeSpan? SilenceFor);

    public static Verdict Classify(WatcherEngine? engine, bool enabled, DateTimeOffset now)
    {
        if (engine is null)
            return new Verdict(enabled ? "EngineDown" : "Disabled", Stalled: false, SilenceFor: null);

        // Only monitors that have an address are expected to produce results;
        // a hostname still resolving is not silence.
        var anyActive = engine.Monitors.Values.Any(m => m.ResolvedAddress is not null);
        if (!anyActive)
            return new Verdict("Healthy", Stalled: false, SilenceFor: null);

        var since = engine.LastResultUtc ?? engine.StartedUtc;
        var silence = now - since;
        return silence > engine.StallAfter
            ? new Verdict("EngineStalled", Stalled: true, SilenceFor: silence)
            : new Verdict("Healthy", Stalled: false, SilenceFor: silence);
    }
}
