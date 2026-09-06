using ConEngine.Topology;
using ConPersistence.Entities;
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

/// <summary>Everything the printable ISP report renders.</summary>
public record IspReport(DateTime FromUtc, DateTime ToUtc,
    List<string> PathNames, List<string> AnchorNames,
    TimeSpan LinkDownTime, TimeSpan LinkDegradedTime, TimeSpan WeatherTime,
    double? AnchorUptimePercent, List<ReportEpisode> Episodes, List<TargetReportRow> Targets,
    GlitchSummary Glitches);

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
        var (pathNames, anchorNames) = await BuildTopologyAsync(db);
        var targets = await BuildTargetRowsAsync(db, fromUtc, toUtc, anchorNames);

        var anchorRows = targets.Where(t => t.IsAnchor).ToList();
        double? anchorUptime = await AnchorUptimeAsync(db, fromUtc, toUtc, anchorNames);

        return new IspReport(fromUtc, toUtc, pathNames, anchorNames,
            totals.GetValueOrDefault("LinkDown"), totals.GetValueOrDefault("LinkDegraded"),
            totals.GetValueOrDefault("InternetWeather"), anchorUptime, episodes, targets,
            await BuildGlitchSummaryAsync(db, fromUtc, toUtc));
    }

    private static async Task<GlitchSummary> BuildGlitchSummaryAsync(
        ConWatchDbContext db, DateTime fromUtc, DateTime toUtc)
    {
        var glitches = await db.Glitches.AsNoTracking()
            .Where(g => g.StartUtc >= fromUtc && g.StartUtc <= toUtc)
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

    private async Task<(List<string> Path, List<string> Anchors)> BuildTopologyAsync(ConWatchDbContext db)
    {
        var anchorPoolName = await settings.GetAsync(SettingKeys.AnchorPool);
        var pool = anchorPoolName is { Length: > 0 }
            ? await db.Pools.AsNoTracking().Include(p => p.Members)
                .SingleOrDefaultAsync(p => p.Name == anchorPoolName)
            : null;
        if (pool is null)
            return ([], []);

        var targets = await db.Targets.AsNoTracking().ToDictionaryAsync(t => t.Id);
        var anchors = pool.Members
            .Where(m => targets.ContainsKey(m.TargetId))
            .OrderBy(m => m.MemberIndex)
            .Select(m => targets[m.TargetId].Name).ToList();

        // Trunk: pool parent (or unique shared member parent) walked to the root.
        var leafId = pool.ParentId ?? pool.Members
            .Select(m => targets.GetValueOrDefault(m.TargetId)?.ParentId)
            .Distinct().SingleOrDefault();
        var path = new List<string>();
        var guard = new HashSet<Guid>();
        while (leafId is { } id && targets.TryGetValue(id, out var node) && guard.Add(id))
        {
            path.Add(node.Name);
            leafId = node.ParentId;
        }
        path.Reverse();
        return (path, anchors);
    }

    private static async Task<List<TargetReportRow>> BuildTargetRowsAsync(ConWatchDbContext db,
        DateTime fromUtc, DateTime toUtc, List<string> anchorNames)
    {
        var rows = new List<TargetReportRow>();
        foreach (var target in await db.Targets.AsNoTracking().OrderBy(t => t.Name).ToListAsync())
        {
            var transitions = await db.Transitions.AsNoTracking()
                .Where(t => t.SubjectId == target.Id && t.TimestampUtc <= toUtc)
                .OrderBy(t => t.Id).ToListAsync();

            // Clip at the latest HistoryReset (address correction/repoint).
            var resetAt = transitions.LastOrDefault(t => t.Trigger == "HistoryReset")?.TimestampUtc;
            var effectiveFrom = resetAt is { } reset && reset > fromUtc ? reset : fromUtc;
            var effective = transitions.Where(t => t.TimestampUtc > effectiveFrom).ToList();

            var stats = ComputeIncidents(effective, toUtc);
            var uptime = await UptimeAsync(db, target.Id, effectiveFrom, toUtc);
            rows.Add(new TargetReportRow(target.Name, target.Address,
                anchorNames.Contains(target.Name), uptime,
                stats.OutageCount, stats.LongestOutage, stats.UnreachableTime));
        }
        return rows;
    }

    private static async Task<double?> UptimeAsync(ConWatchDbContext db, Guid targetId,
        DateTime fromUtc, DateTime toUtc)
    {
        var totals = await db.Rollups.AsNoTracking()
            .Where(r => r.TargetId == targetId && r.PeriodStartUtc >= fromUtc && r.PeriodStartUtc <= toUtc)
            .GroupBy(_ => 1)
            .Select(g => new { Pings = g.Sum(r => r.PingCount), Successes = g.Sum(r => r.SuccessCount) })
            .SingleOrDefaultAsync();
        return totals is { Pings: > 0 } ? 100.0 * totals.Successes / totals.Pings : null;
    }

    private async Task<double?> AnchorUptimeAsync(ConWatchDbContext db, DateTime fromUtc,
        DateTime toUtc, List<string> anchorNames)
    {
        long pings = 0, successes = 0;
        var anchorIds = await db.Targets.AsNoTracking()
            .Where(t => anchorNames.Contains(t.Name)).Select(t => t.Id).ToListAsync();
        foreach (var id in anchorIds)
        {
            var transitions = await db.Transitions.AsNoTracking()
                .Where(t => t.SubjectId == id && t.TimestampUtc <= toUtc && t.Trigger == "HistoryReset")
                .OrderBy(t => t.Id).ToListAsync();
            var resetAt = transitions.LastOrDefault()?.TimestampUtc;
            var effectiveFrom = resetAt is { } reset && reset > fromUtc ? reset : fromUtc;
            var totals = await db.Rollups.AsNoTracking()
                .Where(r => r.TargetId == id && r.PeriodStartUtc >= effectiveFrom && r.PeriodStartUtc <= toUtc)
                .GroupBy(_ => 1)
                .Select(g => new { Pings = g.Sum(r => r.PingCount), Successes = g.Sum(r => r.SuccessCount) })
                .SingleOrDefaultAsync();
            pings += totals?.Pings ?? 0;
            successes += totals?.Successes ?? 0;
        }
        return pings > 0 ? 100.0 * successes / pings : null;
    }

    private record IncidentTotals(int OutageCount, TimeSpan LongestOutage, TimeSpan UnreachableTime);

    /// <summary>C# twin of the History page's split incident semantics.</summary>
    private static IncidentTotals ComputeIncidents(IReadOnlyList<TransitionRow> orderedRows, DateTime rangeEndUtc)
    {
        int outageCount = 0;
        var longestOutage = TimeSpan.Zero;
        var unreachable = TimeSpan.Zero;
        (string Kind, DateTime Start)? open = null;

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
            else if (row.Trigger == "ParentRecovered" && open is { Kind: "unreach" })
            {
                Close(row.TimestampUtc);
            }
        }
        Close(rangeEndUtc);
        return new IncidentTotals(outageCount, longestOutage, unreachable);
    }
}
