using ConCore.Targets;
using ConEngine.Topology;
using ConPersistence.Entities;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Report;

/// <summary>One attributable episode from the verdict history.</summary>
public record ReportEpisode(string Kind, DateTime StartUtc, DateTime EndUtc, string? Evidence)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>Per-target evidence for the report period.</summary>
public record TargetReportRow(string Name, string Address, bool IsAnchor,
    double? UptimePercent, int OutageCount, TimeSpan LongestOutage, TimeSpan UnreachableTime);

/// <summary>Brief-disturbance evidence: counts and clustering of glitches.</summary>
public record GlitchSummary(int TotalCount, int InternetCount,
    List<(int HourUtc, int Count)> TopHours, List<string> ChronicTargets);

/// <summary>Everything the printable ISP report renders. OperatorName is the
/// distinct Operator tag(s) on the trunk targets ("Comcast", or "A / B").</summary>
public record IspReport(DateTime FromUtc, DateTime ToUtc,
    List<string> PathNames, List<string> AnchorNames,
    TimeSpan LinkDownTime, TimeSpan LinkDegradedTime, TimeSpan WeatherTime,
    double? AnchorUptimePercent, List<ReportEpisode> Episodes, List<TargetReportRow> Targets,
    GlitchSummary Glitches, string? OperatorName = null, TimeSpan DnsDownTime = default);

/// <summary>
/// Builds the ISP evidence report. Attribution comes from the verdict history:
/// LinkDown/LinkDegraded is the line, InternetWeather is widespread trouble,
/// and SiteIncident is deliberately excluded — one site's bad day is not the
/// provider's fault. Per-target stats honor HistoryReset clipping.
/// </summary>
public class IspReportBuilder(IDbContextFactory<ConWatchDbContext> dbFactory, SettingsStore settings)
{
    private static readonly HashSet<string> AttributedKinds =
        ["LinkDown", "LinkDegraded", "InternetWeather"];

    public async Task<IspReport> BuildAsync(DateTime fromUtc, DateTime toUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var (episodes, totals) = await BuildEpisodesAsync(db, fromUtc, toUtc);
        var (pathNames, anchorNames, operatorName) = await BuildTopologyAsync(db);
        var (targets, anchorUptime) = await BuildTargetRowsAsync(db, fromUtc, toUtc, anchorNames);

        return new IspReport(fromUtc, toUtc, pathNames, anchorNames,
            totals.GetValueOrDefault("LinkDown"), totals.GetValueOrDefault("LinkDegraded"),
            totals.GetValueOrDefault("InternetWeather"), anchorUptime, episodes, targets,
            await BuildGlitchSummaryAsync(db, fromUtc, toUtc), operatorName,
            DnsDownTime: totals.GetValueOrDefault("DnsDown"));
    }

    private static async Task<GlitchSummary> BuildGlitchSummaryAsync(
        ConWatchDbContext db, DateTime fromUtc, DateTime toUtc)
    {
        // Local targets (including ones switched from Verdict, whose old glitches remain)
        // are not ISP evidence. Internet-class glitches carry the verdict subject id,
        // which is not in Targets, so they are kept.
        var localIds = await db.Targets.AsNoTracking()
            .Where(t => t.Scope == TargetScope.Local).Select(t => t.Id).ToListAsync();
        var glitches = await db.Glitches.AsNoTracking()
            .Where(g => g.StartUtc >= fromUtc && g.StartUtc <= toUtc && !localIds.Contains(g.TargetId))
            .ToListAsync();

        var topHours = glitches
            .GroupBy(g => g.StartUtc.Hour)
            .Select(group => (HourUtc: group.Key, Count: group.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.HourUtc)
            .Take(3)
            .ToList();

        // Chronic: target-class glitches in >= 3 distinct hours of the last
        // 24h of the report period.
        var chronicCutoff = toUtc.AddHours(-24);
        var chronic = glitches
            .Where(g => g.Class == 0 && g.StartUtc >= chronicCutoff)
            .GroupBy(g => g.TargetName)
            .Where(group => group.Select(g => (g.StartUtc.Date, g.StartUtc.Hour)).Distinct().Count() >= 3)
            .Select(group => group.Key)
            .OrderBy(name => name)
            .ToList();

        return new GlitchSummary(glitches.Count, glitches.Count(g => g.Class == 1), topHours, chronic);
    }

    private static async Task<(List<ReportEpisode>, Dictionary<string, TimeSpan>)> BuildEpisodesAsync(
        ConWatchDbContext db, DateTime fromUtc, DateTime toUtc)
    {
        var verdictId = VerdictCoordinator.SubjectId;
        var before = await db.Transitions.AsNoTracking()
            .Where(t => t.SubjectId == verdictId && t.TimestampUtc <= fromUtc)
            .OrderByDescending(t => t.Id).FirstOrDefaultAsync();
        var inRange = await db.Transitions.AsNoTracking()
            .Where(t => t.SubjectId == verdictId && t.TimestampUtc > fromUtc && t.TimestampUtc <= toUtc)
            .OrderBy(t => t.Id).ToListAsync();

        var episodes = new List<ReportEpisode>();
        var totals = new Dictionary<string, TimeSpan>();

        var state = before?.Verdict ?? "Unknown";
        var evidence = before?.Detail;
        var cursor = fromUtc;
        void CloseSegment(DateTime end)
        {
            if (end > cursor && AttributedKinds.Contains(state))
            {
                episodes.Add(new ReportEpisode(state, cursor, end, evidence));
                totals[state] = totals.GetValueOrDefault(state) + (end - cursor);
            }
            else if (end > cursor && state == "DnsDown")
            {
                // The user's resolver, not the connection: its own total, no episode.
                totals["DnsDown"] = totals.GetValueOrDefault("DnsDown") + (end - cursor);
            }
        }
        foreach (var row in inRange)
        {
            CloseSegment(row.TimestampUtc);
            cursor = row.TimestampUtc;
            state = row.Verdict ?? state;
            evidence = row.Detail;
        }
        CloseSegment(toUtc);
        return (episodes, totals);
    }

    private async Task<(List<string> Path, List<string> Anchors, string? OperatorName)> BuildTopologyAsync(ConWatchDbContext db)
    {
        var anchorPoolName = await settings.GetAsync(SettingKeys.AnchorPool);
        var pool = anchorPoolName is { Length: > 0 }
            ? await db.Pools.AsNoTracking().Include(p => p.Members)
                .SingleOrDefaultAsync(p => p.Name == anchorPoolName)
            : null;
        if (pool is null)
            return ([], [], null);

        var targets = await db.Targets.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var anchors = pool.Members
            .Where(m => targets.ContainsKey(m.TargetId))
            .OrderBy(m => m.MemberIndex)
            .Select(m => targets[m.TargetId].Name).ToList();

        // Trunk: pool parent (or unique shared member parent) walked to the root.
        var leafId = pool.ParentId ?? pool.Members
            .Select(m => targets.GetValueOrDefault(m.TargetId)?.ParentId)
            .Distinct().SingleOrDefault();
        var leaf = leafId is { } id ? targets.GetValueOrDefault(id) : null;
        var chain = leaf is null
            ? []
            : TreeWalk.ChainToRoot(leaf, n => n.ParentId is { } pid ? targets.GetValueOrDefault(pid) : null);
        var path = chain.Select(n => n.Name).ToList();
        path.Reverse();
        var operators = chain.Select(n => n.Operator).OfType<string>()
            .Where(op => op.Length > 0).Distinct().ToList();
        return (path, anchors, operators.Count > 0 ? string.Join(" / ", operators) : null);
    }

    /// <summary>
    /// Per-target rows plus the pooled anchor uptime, from three round trips
    /// regardless of target count: every target's transitions up to the
    /// window end (one query), then rollup sums grouped by target, one query
    /// per distinct clipped window start (usually just one, since only a
    /// HistoryReset inside the window moves a target's start).
    /// </summary>
    private static async Task<(List<TargetReportRow> Rows, double? AnchorUptime)> BuildTargetRowsAsync(
        ConWatchDbContext db, DateTime fromUtc, DateTime toUtc, List<string> anchorNames)
    {
        var targets = await db.Targets.AsNoTracking()
            .Where(t => t.Scope == TargetScope.Verdict)
            .OrderBy(t => t.Name).ToListAsync();
        var targetIds = targets.Select(t => t.Id).ToList();

        var transitionsBySubject = (await db.Transitions.AsNoTracking()
                .Where(t => t.TimestampUtc <= toUtc && targetIds.Contains(t.SubjectId))
                .OrderBy(t => t.Id).ToListAsync())
            .GroupBy(t => t.SubjectId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Clip each target at its latest HistoryReset (address correction/repoint).
        var effectiveFrom = new Dictionary<Guid, DateTime>();
        foreach (var target in targets)
        {
            var resetAt = transitionsBySubject.GetValueOrDefault(target.Id)?
                .LastOrDefault(t => t.Trigger == "HistoryReset")?.TimestampUtc;
            effectiveFrom[target.Id] = resetAt is { } reset && reset > fromUtc ? reset : fromUtc;
        }

        var totals = new Dictionary<Guid, (long Pings, long Successes)>();
        foreach (var window in effectiveFrom.GroupBy(kv => kv.Value))
        {
            var ids = window.Select(kv => kv.Key).ToList();
            var start = window.Key;
            var sums = await db.Rollups.AsNoTracking()
                .Where(r => ids.Contains(r.TargetId) && r.PeriodStartUtc >= start && r.PeriodStartUtc <= toUtc)
                .GroupBy(r => r.TargetId)
                .Select(g => new { TargetId = g.Key, Pings = g.Sum(r => r.PingCount), Successes = g.Sum(r => r.SuccessCount) })
                .ToListAsync();
            foreach (var s in sums)
                totals[s.TargetId] = (s.Pings, s.Successes);
        }

        var rows = new List<TargetReportRow>();
        foreach (var target in targets)
        {
            var all = transitionsBySubject.GetValueOrDefault(target.Id) ?? [];
            var start = effectiveFrom[target.Id];
            // The state the target was in when the (clipped) window opened, so
            // an outage that began before it is counted from the window start —
            // the same rule BuildEpisodesAsync applies to the link verdict.
            var openingState = all.LastOrDefault(t => t.TimestampUtc <= start)?.ToState;
            var effective = all.Where(t => t.TimestampUtc > start).ToList();
            var stats = ComputeIncidents(effective, start, toUtc, openingState);
            double? uptime = totals.TryGetValue(target.Id, out var t) && t.Pings > 0
                ? 100.0 * t.Successes / t.Pings : null;
            rows.Add(new TargetReportRow(target.Name, target.Address,
                anchorNames.Contains(target.Name), uptime,
                stats.OutageCount, stats.LongestOutage, stats.UnreachableTime));
        }

        // Anchor uptime pools the anchors' samples over the same clipped windows.
        long pings = 0, successes = 0;
        foreach (var anchor in targets.Where(t => anchorNames.Contains(t.Name)))
        {
            if (totals.TryGetValue(anchor.Id, out var t))
            {
                pings += t.Pings;
                successes += t.Successes;
            }
        }
        double? anchorUptime = pings > 0 ? 100.0 * successes / pings : null;
        return (rows, anchorUptime);
    }

    private record IncidentTotals(int OutageCount, TimeSpan LongestOutage, TimeSpan UnreachableTime);

    /// <summary>C# twin of the History page's split incident semantics. An
    /// outage or unreachable window already open at rangeStart counts from
    /// rangeStart (owner decision 2026-09-18: one rule everywhere).</summary>
    private static IncidentTotals ComputeIncidents(IReadOnlyList<TransitionRow> orderedRows,
        DateTime rangeStartUtc, DateTime rangeEndUtc, string? openingState)
    {
        int outageCount = 0;
        var longestOutage = TimeSpan.Zero;
        var unreachable = TimeSpan.Zero;
        (string Kind, DateTime Start)? open = openingState switch
        {
            "Down" => ("outage", rangeStartUtc),
            "Unreachable" => ("unreach", rangeStartUtc),
            _ => null,
        };
        if (open is { Kind: "outage" })
            outageCount++;

        void Close(DateTime at)
        {
            if (open is not { } window)
                return;
            if (window.Kind == "outage")
                longestOutage = TimeSpan.FromTicks(Math.Max(longestOutage.Ticks, (at - window.Start).Ticks));
            else
                unreachable += at - window.Start;
            open = null;
        }

        foreach (var row in orderedRows)
        {
            if (row.ToState == "Down")
            {
                if (open is { Kind: "unreach" })
                    Close(row.TimestampUtc);
                if (open is null)
                {
                    open = ("outage", row.TimestampUtc);
                    outageCount++;
                }
            }
            else if (row.ToState == "Unreachable")
            {
                Close(row.TimestampUtc);
                open = ("unreach", row.TimestampUtc);
            }
            else if (row.ToState == "Responding")
            {
                Close(row.TimestampUtc);
            }
            else if (open is { Kind: "unreach" } &&
                     (row.Trigger == "ParentRecovered" || row.FromState == "Unreachable"))
            {
                // The release, or the target answering again (Unreachable ->
                // Recovering): the explained window ends either way.
                Close(row.TimestampUtc);
            }
        }
        Close(rangeEndUtc);
        return new IncidentTotals(outageCount, longestOutage, unreachable);
    }
}
