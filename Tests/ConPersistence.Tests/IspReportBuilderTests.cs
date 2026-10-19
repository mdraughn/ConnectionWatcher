using ConCore.Targets;
using ConEngine.Topology;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Report;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class IspReportBuilderTests : IAsyncLifetime, IDisposable
{
    private static readonly DateTime From = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;
    private readonly SettingsStore _settings;
    private readonly IspReportBuilder _builder;
    private Guid _googleId;
    private Guid _cloudflareId;

    public IspReportBuilderTests()
    {
        _store = new ConfigStore(_factory);
        _settings = new SettingsStore(_factory);
        _builder = new IspReportBuilder(_factory, _settings);
    }

    public async Task InitializeAsync()
    {
        await _store.SeedAsync(ConfigStoreTests.ValidHome()); // Router->Modem->{Google,Cloudflare}, pool Internet
        await _settings.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.AnchorPool] = "Internet",
        });
        var options = await _store.LoadWatcherOptionsAsync();
        _googleId = options.Targets.Single(t => t.Name == "Google").Id!.Value;
        _cloudflareId = options.Targets.Single(t => t.Name == "Cloudflare").Id!.Value;
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private async Task AddVerdict(DateTime at, string? verdict, string? detail = null)
    {
        await using var db = _factory.CreateDbContext();
        db.Transitions.Add(new TransitionRow
        {
            TimestampUtc = at, SubjectId = VerdictCoordinator.SubjectId, SubjectName = "Internet",
            SubjectKind = "Verdict", FromState = "Responding", ToState = "Responding",
            Trigger = "VerdictChanged", Verdict = verdict, Detail = detail,
        });
        await db.SaveChangesAsync();
    }

    private async Task AddTransition(Guid subject, string name, DateTime at, string to,
        string trigger = "ThresholdCrossed", string from = "Responding")
    {
        await using var db = _factory.CreateDbContext();
        db.Transitions.Add(new TransitionRow
        {
            TimestampUtc = at, SubjectId = subject, SubjectName = name, SubjectKind = "Target",
            FromState = from, ToState = to, Trigger = trigger,
        });
        await db.SaveChangesAsync();
    }

    private async Task AddRollup(Guid target, DateTime period, int pings, int successes, string resolution = "Hour")
    {
        await using var db = _factory.CreateDbContext();
        db.Rollups.Add(new RollupRow
        {
            TargetId = target, PeriodStartUtc = period, Resolution = resolution,
            PingCount = pings, SuccessCount = successes, RttAvg = 10, RttMin = 5, RttMax = 20,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Episodes_are_attributed_and_site_incidents_excluded()
    {
        await AddVerdict(From.AddHours(-2), "Healthy");
        await AddVerdict(From.AddHours(1), "LinkDown", "LinkDown: Modem is Down");
        await AddVerdict(From.AddHours(2), "Healthy");
        await AddVerdict(From.AddHours(5), "InternetWeather", "2 of 2 anchors degraded");
        await AddVerdict(From.AddHours(6), "Healthy");
        await AddVerdict(From.AddHours(8), "SiteIncident", "Google=Down");
        await AddVerdict(From.AddHours(9), "Healthy");

        var report = await _builder.BuildAsync(From, To);

        Assert.Equal(2, report.Episodes.Count);
        var linkDown = report.Episodes[0];
        Assert.Equal("LinkDown", linkDown.Kind);
        Assert.Equal(TimeSpan.FromHours(1), linkDown.Duration);
        Assert.Contains("Modem", linkDown.Evidence);
        Assert.Equal("InternetWeather", report.Episodes[1].Kind);
        Assert.Equal(TimeSpan.FromHours(1), report.LinkDownTime);
        Assert.Equal(TimeSpan.FromHours(1), report.WeatherTime);
        Assert.Equal(TimeSpan.Zero, report.LinkDegradedTime);
    }

    [Fact]
    public async Task Episode_spanning_range_start_is_clipped_to_range()
    {
        await AddVerdict(From.AddHours(-3), "LinkDown", "LinkDown: Router is Down");
        await AddVerdict(From.AddHours(1), "Healthy");

        var report = await _builder.BuildAsync(From, To);

        var episode = Assert.Single(report.Episodes);
        Assert.Equal(From, episode.StartUtc);
        Assert.Equal(TimeSpan.FromHours(1), episode.Duration);
    }

    [Fact]
    public async Task Target_rows_honor_history_reset_clipping()
    {
        await AddTransition(_googleId, "Google", From.AddHours(1), "Down");
        await AddTransition(_googleId, "Google", From.AddHours(2), "Responding");
        await AddTransition(_googleId, "Google", From.AddHours(3), "Starting", "HistoryReset");
        await AddTransition(_googleId, "Google", From.AddHours(4), "Down"); // open to range end

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(1, google.OutageCount);                       // pre-reset outage excluded
        Assert.Equal(TimeSpan.FromHours(20), google.LongestOutage); // 04:00 -> 24:00
    }

    [Fact]
    public async Task Target_outage_open_at_the_window_start_counts_from_the_window_start()
    {
        await AddTransition(_googleId, "Google", From.AddHours(-2), "Down");
        await AddTransition(_googleId, "Google", From.AddHours(1), "Responding");

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(1, google.OutageCount);
        Assert.Equal(TimeSpan.FromHours(1), google.LongestOutage);
    }

    [Fact]
    public async Task An_unreachable_target_answering_again_closes_its_unreachable_window()
    {
        // Final review M5: Unreachable -> Recovering ends the explained window,
        // as a release or a recovery does.
        await AddTransition(_googleId, "Google", From.AddHours(1), "Unreachable", "ParentDown", from: "Faltering");
        await AddTransition(_googleId, "Google", From.AddHours(2), "Recovering", from: "Unreachable");
        await AddTransition(_googleId, "Google", From.AddHours(3), "Responding", from: "Recovering");

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(TimeSpan.FromHours(1), google.UnreachableTime);
        Assert.Equal(0, google.OutageCount);
    }

    // One step: minutes after From, the target (or verdict) transition it records.
    private sealed record Step(int Minute, string To, string From = "Responding",
        string Trigger = "ThresholdCrossed", string? Verdict = null);

    private sealed record IncidentCase(Step[] Steps, int RangeMinutes, int Outages,
        int LongestMinutes, int UnreachableMinutes);

    // Mirrors the nine cases of "incidents (split semantics)" in
    // ClientApp/src/lib/historyData.test.ts, in the same order, same range.
    private static readonly Dictionary<string, IncidentCase> IncidentCases = new()
    {
        ["1 a Down window is an outage, measured to recovery"] = new(
            [new(60, "Down"), new(70, "Recovering", "Down"), new(72, "Responding", "Recovering")],
            360, 1, 12, 0),
        ["2 unreachable time ends at the release, not at re-prove"] = new(
            [new(300, "Unreachable", "Responding", "ParentDown"),
             new(301, "Starting", "Unreachable", "ParentRecovered"),
             new(303, "Responding", "Starting")],
            360, 0, 0, 1),
        ["3 unreachable time ends when the target answers again"] = new(
            [new(300, "Unreachable", "Faltering", "ParentDown"),
             new(302, "Recovering", "Unreachable"),
             new(310, "Responding", "Recovering")],
            360, 0, 0, 2),
        ["4 an open outage runs to range end"] = new(
            [new(60, "Down")], 120, 1, 60, 0),
        ["5 an outage superseded by unreachable splits into both buckets"] = new(
            [new(60, "Down"), new(65, "Unreachable", "Down", "ParentDown"),
             new(70, "Starting", "Unreachable", "ParentRecovered"),
             new(72, "Responding", "Starting")],
            360, 1, 5, 5),
        ["6 a renumber marker does not end an open outage"] = new(
            [new(60, "Down"), new(63, "Starting", "Starting", "AddressChanged"),
             new(65, "Responding", "Starting")],
            360, 1, 5, 0),
        // The client counts this as an outage from the range start, inferring the
        // opening state from the row's FromState. The report reads the opening
        // state from the last row at or before the range start, and there is none,
        // so it counts nothing. Ruling D6: the report's rule is right; Task 33
        // aligns History to it. Pinned at the report's answer.
        ["7 an outage already open at the range start (no row before it)"] = new(
            [new(60, "Responding", "Down")], 360, 0, 0, 0),
        // AUDIT: the report's per-target rows read target transitions only. A
        // Verdict-kind row (DnsDown) lives under the verdict's own subject and
        // can never reach a target's incident count, so the client's DnsDown rule
        // holds structurally here, not by an explicit check.
        ["8 a DnsDown verdict is never an outage"] = new(
            [new(60, "Down", "Responding", "VerdictChanged", "DnsDown"),
             new(80, "Responding", "Down", "VerdictChanged", "Healthy")],
            360, 0, 0, 0),
        // AUDIT: same structural exclusion. The client expects 2 outages / 5 min
        // for verdict rows; the report's target rows see none of them.
        ["9 DnsDown closes a LinkDown outage; LinkDown after it opens one"] = new(
            [new(60, "Down", "Responding", "VerdictChanged", "LinkDown"),
             new(65, "Down", "Down", "VerdictChanged", "DnsDown"),
             new(120, "Down", "Down", "VerdictChanged", "LinkDown"),
             new(122, "Responding", "Down", "VerdictChanged", "Healthy")],
            360, 0, 0, 0),
        // Not a client case: the report's own rule when a target goes from
        // Unreachable straight to Down (the explained window closes, an outage opens).
        ["10 report only: Unreachable -> Down closes the window and opens an outage"] = new(
            [new(60, "Unreachable", "Responding", "ParentDown"),
             new(70, "Down", "Unreachable"),
             new(90, "Responding", "Down")],
            360, 1, 20, 10),
    };

    public static TheoryData<string> IncidentCaseNames => new(IncidentCases.Keys);

    [Theory]
    [MemberData(nameof(IncidentCaseNames))]
    public async Task Incidents_match_the_history_page_cases(string name)
    {
        var c = IncidentCases[name];
        foreach (var step in c.Steps)
        {
            var at = From.AddMinutes(step.Minute);
            if (step.Verdict is null)
            {
                await AddTransition(_googleId, "Google", at, step.To, step.Trigger, step.From);
                continue;
            }
            await using var db = _factory.CreateDbContext();
            db.Transitions.Add(new TransitionRow
            {
                TimestampUtc = at, SubjectId = VerdictCoordinator.SubjectId, SubjectName = "Internet",
                SubjectKind = "Verdict", FromState = step.From, ToState = step.To,
                Trigger = step.Trigger, Verdict = step.Verdict,
            });
            await db.SaveChangesAsync();
        }

        var report = await _builder.BuildAsync(From, From.AddMinutes(c.RangeMinutes));

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(c.Outages, google.OutageCount);
        Assert.Equal(TimeSpan.FromMinutes(c.LongestMinutes), google.LongestOutage);
        Assert.Equal(TimeSpan.FromMinutes(c.UnreachableMinutes), google.UnreachableTime);
    }

    [Fact]
    public async Task Target_down_for_the_whole_window_is_one_outage_the_length_of_the_window()
    {
        await AddTransition(_googleId, "Google", From.AddDays(-3), "Down");

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(1, google.OutageCount);
        Assert.Equal(To - From, google.LongestOutage);
    }

    [Fact]
    public async Task Uptime_uses_post_reset_rollups_and_anchor_aggregate_weights()
    {
        await AddTransition(_googleId, "Google", From.AddHours(3), "Starting", "HistoryReset");
        await AddRollup(_googleId, From.AddHours(1), 100, 10);   // pre-reset garbage: excluded
        await AddRollup(_googleId, From.AddHours(5), 100, 90);   // post-reset
        await AddRollup(_cloudflareId, From.AddHours(5), 100, 100);

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.NotNull(google.UptimePercent);
        Assert.Equal(90.0, google.UptimePercent!.Value, 3);
        Assert.NotNull(report.AnchorUptimePercent);
        Assert.Equal(95.0, report.AnchorUptimePercent!.Value, 3); // (90+100)/200
    }

    [Fact]
    public async Task Path_and_anchor_names_come_from_the_pool()
    {
        var report = await _builder.BuildAsync(From, To);
        Assert.Equal(["Router", "Modem"], report.PathNames);
        Assert.Equal(2, report.AnchorNames.Count);
        Assert.Contains("Google", report.AnchorNames);
    }

    [Fact]
    public async Task Report_names_the_operator_from_the_trunk()
    {
        var before = await _builder.BuildAsync(From, To);
        Assert.Null(before.OperatorName); // no tags yet

        await using (var db = _factory.CreateDbContext())
        {
            var modem = await db.Targets.SingleAsync(t => t.Name == "Modem");
            modem.Operator = "Comcast";
            await db.SaveChangesAsync();
        }

        var report = await _builder.BuildAsync(From, To);
        Assert.Equal("Comcast", report.OperatorName);
    }

    [Fact]
    public async Task Glitch_summary_counts_top_hours_and_chronic_targets()
    {
        await using (var db = _factory.CreateDbContext())
        {
            // Nightly stripe: 3 glitches at 03:xx UTC across the period, one at 14:xx.
            var day = From;
            for (int i = 0; i < 3; i++)
                db.Glitches.Add(new GlitchRow { TargetId = _googleId, TargetName = "Google", Class = 0,
                    StartUtc = day.AddHours(3 + 0.1 * i), EndUtc = day.AddHours(3.2), LostCount = 2, SampleCount = 8 });
            db.Glitches.Add(new GlitchRow { TargetId = _cloudflareId, TargetName = "Cloudflare", Class = 0,
                StartUtc = day.AddHours(14), EndUtc = day.AddHours(14.01), LostCount = 2, SampleCount = 8 });
            db.Glitches.Add(new GlitchRow { TargetId = Guid.NewGuid(), TargetName = "Internet", Class = 1,
                StartUtc = day.AddHours(3.05), EndUtc = day.AddHours(3.15) });
            // Chronic: Google glitches in 3 distinct hours within 24h of the period end.
            db.Glitches.Add(new GlitchRow { TargetId = _googleId, TargetName = "Google", Class = 0,
                StartUtc = To.AddHours(-2), EndUtc = To.AddHours(-2), LostCount = 2, SampleCount = 8 });
            db.Glitches.Add(new GlitchRow { TargetId = _googleId, TargetName = "Google", Class = 0,
                StartUtc = To.AddHours(-4), EndUtc = To.AddHours(-4), LostCount = 2, SampleCount = 8 });
            await db.SaveChangesAsync();
        }

        var report = await _builder.BuildAsync(From, To);
        Assert.Equal(7, report.Glitches.TotalCount);
        Assert.Equal(1, report.Glitches.InternetCount);
        Assert.Equal(3, report.Glitches.TopHours[0].HourUtc);   // 03:00 UTC dominates
        Assert.Equal(4, report.Glitches.TopHours[0].Count);     // 3 target + 1 internet
        Assert.Equal(["Google"], report.Glitches.ChronicTargets); // 03:xx + 20:00 + 22:00 of the last day
    }

    [Fact]
    public async Task Local_targets_and_their_history_stay_out_of_the_report()
    {
        var service = new ConfigService(_factory, _store);
        Assert.True((await service.UpsertTargetAsync(new TargetDto
        {
            Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local,
        })).Success);
        var nasId = (await _store.LoadWatcherOptionsAsync()).Targets.Single(t => t.Name == "NAS").Id!.Value;

        await AddTransition(nasId, "NAS", From.AddHours(2), "Down");
        await AddRollup(nasId, From.AddHours(1), 100, 50);
        await using (var db = _factory.CreateDbContext())
        {
            // Three class-0 glitches in distinct hours late in the period would make NAS "chronic".
            for (int i = 0; i < 3; i++)
                db.Glitches.Add(new GlitchRow { TargetId = nasId, TargetName = "NAS", Class = 0,
                    StartUtc = To.AddHours(-1 - i), EndUtc = To.AddHours(-1 - i), LostCount = 2, SampleCount = 8 });
            await db.SaveChangesAsync();
        }

        var report = await _builder.BuildAsync(From, To);

        Assert.DoesNotContain(report.Targets, r => r.Name == "NAS");
        Assert.Equal(0, report.Glitches.TotalCount);
        Assert.DoesNotContain("NAS", report.Glitches.ChronicTargets);
        Assert.Contains(report.Targets, r => r.Name == "Google"); // verdict targets still reported
    }

    [Fact]
    public async Task DnsDown_time_is_its_own_figure_and_never_attributed_to_the_ISP()
    {
        var t0 = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        await AddVerdict(t0, "Healthy");
        await AddVerdict(t0.AddMinutes(10), "DnsDown");
        await AddVerdict(t0.AddMinutes(25), "Healthy");
        await AddVerdict(t0.AddMinutes(40), "LinkDown");
        await AddVerdict(t0.AddMinutes(45), "Healthy");

        var report = await _builder.BuildAsync(t0, t0.AddHours(1));

        Assert.Equal(TimeSpan.FromMinutes(15), report.DnsDownTime);
        Assert.Equal(TimeSpan.FromMinutes(5), report.LinkDownTime);
        Assert.DoesNotContain(report.Episodes, e => e.Kind == "DnsDown");
    }

    [Fact]
    public async Task A_deleted_targets_glitches_are_not_the_ISPs_but_Internet_class_ones_are()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Glitches.AddRange(
                new GlitchRow { TargetId = _googleId, TargetName = "Google", Class = 0,
                    StartUtc = From.AddHours(3), EndUtc = From.AddHours(3.01), LostCount = 2, SampleCount = 8 },
                new GlitchRow { TargetId = Guid.NewGuid(), TargetName = "Gone", Class = 0, // no longer configured
                    StartUtc = From.AddHours(4), EndUtc = From.AddHours(4.01), LostCount = 2, SampleCount = 8 },
                new GlitchRow { TargetId = Guid.NewGuid(), TargetName = "Internet", Class = 1, // verdict subject id
                    StartUtc = From.AddHours(5), EndUtc = From.AddHours(5.01), LostCount = 2, SampleCount = 8 });
            await db.SaveChangesAsync();
        }

        var report = await _builder.BuildAsync(From, To);

        Assert.Equal(2, report.Glitches.TotalCount);
        Assert.Equal(1, report.Glitches.InternetCount);
    }

    [Fact]
    public async Task A_verdict_row_without_a_verdict_continues_the_episode()
    {
        // A marker row on the verdict subject (a HistoryReset) carries no verdict: the
        // state in force before it stays in force, so the outage is not cut short.
        await AddVerdict(From.AddHours(1), "LinkDown", "LinkDown: Modem is Down");
        await AddVerdict(From.AddHours(2), null);
        await AddVerdict(From.AddHours(4), "Healthy");

        var report = await _builder.BuildAsync(From, To);

        // AUDIT: the marker row splits the outage into two episodes and the second one loses
        // its evidence text (the marker's Detail replaces it). Totals are right; the listing
        // is not one continuous episode. Pinned as totals and contiguity, not the split.
        Assert.Equal(TimeSpan.FromHours(3), report.LinkDownTime);
        Assert.All(report.Episodes, e => Assert.Equal("LinkDown", e.Kind));
        Assert.Equal(From.AddHours(1), report.Episodes.Min(e => e.StartUtc));
        Assert.Equal(From.AddHours(4), report.Episodes.Max(e => e.EndUtc));
        Assert.Equal(TimeSpan.FromHours(3), report.Episodes.Aggregate(TimeSpan.Zero, (sum, e) => sum + e.Duration));
    }

    [Fact]
    public async Task Uptime_counts_hour_rows_and_minute_rows_across_one_window()
    {
        // The retention pass folds minutes into hours as they age, so one report window
        // straddling the boundary holds both resolutions; both count.
        await AddRollup(_googleId, From.AddHours(1), 100, 90, "Hour");
        await AddRollup(_googleId, From.AddHours(20), 100, 100, "Minute");

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(95.0, google.UptimePercent!.Value, 3); // (90+100)/200
        Assert.Equal(95.0, report.AnchorUptimePercent!.Value, 3);
    }

    [Fact]
    public async Task LinkDegraded_time_is_attributed_and_totalled()
    {
        await AddVerdict(From.AddHours(1), "LinkDegraded", "LinkDegraded: Modem is Faltering");
        await AddVerdict(From.AddHours(3), "Healthy");

        var report = await _builder.BuildAsync(From, To);

        Assert.Equal(TimeSpan.FromHours(2), report.LinkDegradedTime);
        Assert.Equal(TimeSpan.Zero, report.LinkDownTime);
        var episode = Assert.Single(report.Episodes);
        Assert.Equal("LinkDegraded", episode.Kind);
    }
}
