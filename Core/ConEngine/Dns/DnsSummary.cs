using ConEngine.Topology;
using ConEngine.Tracking;

namespace ConEngine.Dns;

public enum DnsSummaryState { Unknown, Ok, Degraded, Filtered, Down }

/// <summary>
/// The dns block's state (spec 2026-10-10 §4.4, §5.1), computed from the
/// system resolver monitors and the result log. One function, so status,
/// MCP, metrics and the notes never disagree. Upstream resolvers never
/// change it (they only decide the DnsDown blame).
/// </summary>
public sealed record DnsSummary(bool Watched, DnsSummaryState State)
{
    public const int FilteredWindow = 10;
    public const int FilteredRaise = 3;

    public static DnsSummary Compute(DnsWatch watch, DnsResultLog log)
    {
        if (!watch.Enabled || watch.System.Count == 0)
            return new DnsSummary(false, DnsSummaryState.Unknown);
        var states = watch.System.Select(m => m.CurrentState).ToList();
        var determined = states.Where(s => !s.IsUndetermined()).ToList();
        if (determined.Count == 0)
            return new DnsSummary(true, DnsSummaryState.Unknown);
        if (determined.All(s => s.IsFailed()))
            return new DnsSummary(true, DnsSummaryState.Down);
        if (determined.Any(s => s is HostState.Down or HostState.Unreachable or HostState.Faltering or HostState.Recovering))
            return new DnsSummary(true, DnsSummaryState.Degraded);
        var answering = watch.System.Where(m => m.CurrentState.IsAnswering()).ToList();
        if (answering.Count > 0 && answering.All(m => IsFiltered(log.Recent(m.Target.Id))))
            return new DnsSummary(true, DnsSummaryState.Filtered);
        return new DnsSummary(true, DnsSummaryState.Ok);
    }

    /// <summary>Over the last <see cref="FilteredWindow"/> judged results — seeded-name
    /// outcomes: Filtered, WrongAnswer or Verified (an admin/hostname name's Answered is
    /// unjudged and never dilutes the window): at least <see cref="FilteredRaise"/> Filtered, and
    /// every name seen Filtered is still Filtered at its latest ask.</summary>
    public static bool IsFiltered(IReadOnlyList<DnsResult> recent)
    {
        var judged = recent.Where(r => r.Outcome is "Filtered" or "WrongAnswer" or "Verified").TakeLast(FilteredWindow).ToList();
        if (judged.Count(r => r.Outcome == "Filtered") < FilteredRaise)
            return false;
        var latestByName = judged.Where(r => r.Name is not null).GroupBy(r => r.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Outcome, StringComparer.OrdinalIgnoreCase);
        var filteredNames = judged.Where(r => r.Outcome == "Filtered" && r.Name is not null).Select(r => r.Name!).Distinct(StringComparer.OrdinalIgnoreCase);
        return filteredNames.All(n => latestByName[n] == "Filtered");
    }
}
