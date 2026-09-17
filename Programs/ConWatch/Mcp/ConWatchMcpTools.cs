using System.ComponentModel;
using System.Text.Json;
using ConPersistence;
using ConPersistence.Report;
using ConWatch.Api;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace ConWatch.Mcp;

/// <summary>
/// Read-only MCP tools over the watching surface, so an MCP host (Claude
/// Code, Claude desktop) can answer "how was my internet last night?" from
/// the actual database. Everything here is data the OPEN APIs already serve;
/// no tool touches settings, secrets, or the locked surface.
/// Spec: docs/superpowers/specs/2026-09-13-mcp-server-design.md.
/// </summary>
[McpServerToolType]
public class ConWatchMcpTools(WatcherHostService host,
    IDbContextFactory<ConWatchDbContext> dbFactory, IspReportBuilder reportBuilder)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "get_current_status", ReadOnly = true)]
    [Description("Live status right now: the overall internet verdict (Healthy, " +
        "SiteIncident, InternetWeather, LinkDegraded, LinkDown) with its evidence and " +
        "blamed ISP when applicable, every monitored target's state, and whether " +
        "monitoring is enabled (a disabled node is a read-only archive). " +
        "Answering 'is my internet up?': Healthy, SiteIncident and InternetWeather all " +
        "mean YES, the user's own connection is up (SiteIncident = one remote site is " +
        "having trouble; InternetWeather = widespread trouble out on the internet, not " +
        "the user's line); LinkDegraded means their connection is losing traffic; " +
        "LinkDown means it is down. " +
        "All timestamps in this server are ISO-8601 UTC; convert to the user's zone when answering.")]
    public async Task<string> GetCurrentStatus()
    {
        var engine = host.Engine;
        var verdict = engine?.Verdict;
        return JsonSerializer.Serialize(new
        {
            monitoringEnabled = await host.EngineEnabledAsync(),
            version = BuildInfo.Version,
            builtUtc = BuildInfo.BuiltUtc,
            verdict = verdict is null ? null : new
            {
                state = verdict.CurrentVerdict.ToString(),
                detail = verdict.CurrentDetail,
                blamedOperator = verdict.CurrentOperator,
            },
            targets = engine?.Monitors.Values.OrderBy(m => m.Target.Name).Select(m => new
            {
                name = m.Target.Name,
                address = m.ResolvedAddress?.ToString() ?? m.Target.Address,
                probe = m.Target.Probe,
                state = m.CurrentState.ToString(),
                assumed = m.IsAssumed,
                successRate = m.LastStatus?.Host.SuccessRate,
                avgRttMs = m.LastStatus?.Host.AverageRoundtripTime,
                @operator = m.Target.Operator,
            }),
        }, Json);
    }

    [McpServerTool(Name = "get_connection_report", ReadOnly = true)]
    [Description("The ISP evidence report for a period, as data: attributable incidents " +
        "(LinkDown = the user's line, LinkDegraded, InternetWeather = widespread trouble " +
        "that is NOT the user's line) with recorded evidence, total downtime figures, " +
        "measured anchor uptime, and a summary of sub-outage glitches. " +
        "Single-site incidents are excluded by design — they are not attributable to the " +
        "connection. Use this first for questions like 'how was my internet last night?'. " +
        "Times are ISO-8601 UTC in and out.")]
    public async Task<string> GetConnectionReport(
        [Description("Period start, ISO-8601 UTC, e.g. 2026-09-12T00:00:00Z")] DateTime fromUtc,
        [Description("Period end, ISO-8601 UTC")] DateTime toUtc)
    {
        var report = await reportBuilder.BuildAsync(
            TimeRange.AsUtc(fromUtc), TimeRange.AsUtc(toUtc));
        return JsonSerializer.Serialize(report, Json);
    }

    [McpServerTool(Name = "get_transitions", ReadOnly = true)]
    [Description("The raw state-transition log (newest first): every recorded state " +
        "change for targets, pools, and the internet verdict, with trigger and evidence. " +
        "Use for detailed digging after get_connection_report. Times are ISO-8601 UTC.")]
    public async Task<string> GetTransitions(
        [Description("Only transitions at/after this ISO-8601 UTC time (optional)")] DateTime? fromUtc = null,
        [Description("Only transitions at/before this ISO-8601 UTC time (optional)")] DateTime? toUtc = null,
        [Description("Filter to one subject by exact name, e.g. 'Router' or 'Internet' (optional)")] string? subjectName = null,
        [Description("Max rows, 1-500 (default 100)")] int limit = 100)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await HistoryQueries.TransitionsAsync(db, subjectId: null,
            fromUtc is { } f ? TimeRange.AsUtc(f) : null,
            toUtc is { } u ? TimeRange.AsUtc(u) : null,
            Math.Clamp(limit, 1, 500), before: null, subjectName);
        return JsonSerializer.Serialize(rows.Select(t => new
        {
            TimestampUtc = DateTime.SpecifyKind(t.TimestampUtc, DateTimeKind.Utc),
            t.SubjectName, t.SubjectKind, t.FromState, t.ToState,
            t.Trigger, t.Detail, t.Verdict,
        }), Json);
    }

    [McpServerTool(Name = "get_glitches", ReadOnly = true)]
    [Description("Sub-outage loss episodes (newest first): brief ping-loss blips too " +
        "small to change any state — 'the page stalled for a second' events. " +
        "class Internet means the blip was corroborated across multiple destinations. " +
        "Times are ISO-8601 UTC.")]
    public async Task<string> GetGlitches(
        [Description("Only glitches starting at/after this ISO-8601 UTC time (optional)")] DateTime? fromUtc = null,
        [Description("Only glitches starting at/before this ISO-8601 UTC time (optional)")] DateTime? toUtc = null,
        [Description("Max rows, 1-500 (default 100)")] int limit = 100)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await HistoryQueries.GlitchesAsync(db, targetId: null,
            fromUtc is { } f ? TimeRange.AsUtc(f) : null,
            toUtc is { } u ? TimeRange.AsUtc(u) : null,
            Math.Clamp(limit, 1, 500));
        return JsonSerializer.Serialize(rows.Select(g => new
        {
            StartUtc = DateTime.SpecifyKind(g.StartUtc, DateTimeKind.Utc),
            EndUtc = DateTime.SpecifyKind(g.EndUtc, DateTimeKind.Utc),
            g.TargetName,
            @class = g.Class == 1 ? "Internet" : "Target",
            g.LostCount, g.SampleCount, g.Detail,
        }), Json);
    }
}
