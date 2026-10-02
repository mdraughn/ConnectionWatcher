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

    private async Task AddVerdict(DateTime at, string verdict, string? detail = null)
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
        string trigger = "ThresholdCrossed")
    {
        await using var db = _factory.CreateDbContext();
        db.Transitions.Add(new TransitionRow
        {
            TimestampUtc = at, SubjectId = subject, SubjectName = name, SubjectKind = "Target",
            FromState = "Responding", ToState = to, Trigger = trigger,
        });
        await db.SaveChangesAsync();
    }

    private async Task AddRollup(Guid target, DateTime period, int pings, int successes)
    {
        await using var db = _factory.CreateDbContext();
        db.Rollups.Add(new RollupRow
        {
            TargetId = target, PeriodStartUtc = period, Resolution = "Hour",
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
}
