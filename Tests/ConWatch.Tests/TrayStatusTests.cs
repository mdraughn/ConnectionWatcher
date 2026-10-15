using ConInfrastructure.Tray;

namespace ConWatch.Tests;

public class TrayStatusTests
{
    private const string Healthy = """{"enabled":true,"verdict":{"state":"Healthy","detail":"Healthy: 2 anchors responding","operator":null},"targets":[],"pools":[]""";

    private static string With(string warnings) => Healthy + $",\"warnings\":{warnings}}}";

    [Fact]
    public void Notes_never_change_the_state_and_only_unacknowledged_ones_count()
    {
        var info = TrayStatus.FromStatusJson(With("""
            [{"key":"hop-bypassed:1","kind":"HopBypassed","targetName":"Inside","message":"m","since":"2026-10-08T23:42:16Z","acknowledged":false},
             {"key":"unprovable-up","kind":"UnprovableUp","targetName":null,"message":"m","since":"2026-10-08T23:42:16Z","acknowledged":true}]
            """));
        Assert.Equal(TrayState.Healthy, info.State);
        Assert.Equal(["Inside isn't answering (traffic passes it)"], info.Notes);
    }

    [Fact]
    public void A_status_without_warnings_has_no_notes()
    {
        Assert.Empty(TrayStatus.FromStatusJson(Healthy + "}").Notes);
        Assert.Empty(TrayStatus.FromStatusJson("not json").Notes);
    }

    [Fact]
    public void Each_kind_has_a_short_label()
    {
        var info = TrayStatus.FromStatusJson(With("""
            [{"key":"a","kind":"HopBypassed","targetName":"Inside","message":"m","since":"x","acknowledged":false},
             {"key":"b","kind":"NeverAnswered","targetName":"Comcast Edge","message":"m","since":"x","acknowledged":false},
             {"key":"c","kind":"UnprovableUp","targetName":null,"message":"m","since":"x","acknowledged":false},
             {"key":"d","kind":"SomethingNew","targetName":null,"message":"m","since":"x","acknowledged":false}]
            """));
        Assert.Equal([
            "Inside isn't answering (traffic passes it)",
            "Comcast Edge never answers its probe",
            "Can't prove the internet is up beyond your ISP",
            "ConnectionWatcher note",
        ], info.Notes);
    }

    [Fact]
    public void Probe_only_loss_has_a_short_label()
    {
        var info = TrayStatus.FromStatusJson(With("""
            [{"key":"p","kind":"ProbeOnlyLoss","targetName":"Router","message":"m","since":"x","acknowledged":false}]
            """));
        Assert.Equal(["Router drops its own pings (forwards fine)"], info.Notes);
    }

    [Fact]
    public void Tooltip_lists_notes_after_the_state_and_detail()
    {
        var info = new TrayInfo(TrayState.Healthy, "Healthy: 2 anchors responding",
            ["Inside isn't answering (traffic passes it)"]);
        Assert.Equal("Internet: healthy\nHealthy: 2 anchors responding\n🔧 Inside isn't answering (traffic passes it)",
            TrayStatus.Tooltip(info));
    }

    [Fact]
    public void Tooltip_never_exceeds_the_notify_icon_limit()
    {
        var info = new TrayInfo(TrayState.Healthy, new string('d', 90),
            Enumerable.Range(0, 6).Select(i => $"A very long target name number {i} isn't answering (traffic passes it)").ToList());
        var text = TrayStatus.Tooltip(info);
        Assert.True(text.Length <= 127, $"{text.Length} chars");
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Tooltip_without_notes_matches_the_old_text()
    {
        Assert.Equal("Internet: DOWN\nLinkDown: Router is Down",
            TrayStatus.Tooltip(new TrayInfo(TrayState.Down, "LinkDown: Router is Down", [])));
    }

    [Theory]
    [InlineData("Healthy", TrayState.Healthy)]
    [InlineData("LinkDegraded", TrayState.Degraded)]
    [InlineData("LinkDown", TrayState.Down)]
    [InlineData("DnsDown", TrayState.Down)]
    [InlineData("Unknown", TrayState.Measuring)]
    public void Verdict_maps_to_the_tray_state(string verdict, TrayState expected) =>
        Assert.Equal(expected, TrayStatus.FromStatusJson(Status("Responding", verdict: verdict)).State);

    private const string RouterId = "11111111-1111-1111-1111-111111111111";

    private static string Status(string routerState, bool critical = true, string verdict = "Healthy",
        string warnings = "[]") =>
        $$"""
        {"enabled":true,"verdict":{"state":"{{verdict}}","detail":"d","operator":null},
         "targets":[{"id":"{{RouterId}}","name":"Router","state":"{{routerState}}","scope":"Verdict","critical":{{(critical ? "true" : "false")}}},
                    {"id":"22222222-2222-2222-2222-222222222222","name":"Google","state":"Responding","scope":"Verdict","critical":false}],
         "pools":[],"warnings":{{warnings}}}
        """;

    [Fact]
    public void Critical_targets_are_parsed_with_whether_traffic_passes_them()
    {
        var info = TrayStatus.FromStatusJson(Status("Down", warnings:
            $$"""[{"key":"hop-bypassed:{{RouterId}}","kind":"HopBypassed","targetId":"{{RouterId}}","targetName":"Router","message":"m","since":"x","acknowledged":true}]"""));
        var router = Assert.Single(info.Critical);
        Assert.Equal("Router", router.Name);
        Assert.True(router.IsDown);
        Assert.True(router.Bypassed); // acknowledged notes still say traffic passes it
        Assert.Equal(TrayState.Healthy, info.State); // critical never changes the state
    }

    [Fact]
    public void Non_critical_targets_and_old_servers_yield_no_critical_entries()
    {
        Assert.Empty(TrayStatus.FromStatusJson(Status("Down", critical: false)).Critical);
        Assert.Empty(TrayStatus.FromStatusJson(Healthy + "}").Critical); // no targets[] critical keys
        Assert.Empty(TrayStatus.FromStatusJson("not json").Critical);
    }

    [Fact]
    public void Tooltip_lists_down_critical_targets_before_notes()
    {
        var info = new TrayInfo(TrayState.Healthy, "d", ["Router isn't answering (traffic passes it)"])
        {
            Critical = [new TrayCritical(RouterId, "Router", "Down", Bypassed: true)],
        };
        Assert.Equal("Internet: healthy\n⚠ Router is Down\nd\n🔧 Router isn't answering (traffic passes it)",
            TrayStatus.Tooltip(info));
    }

    [Fact]
    public void A_malformed_critical_entry_is_skipped_not_fatal()
    {
        var info = TrayStatus.FromStatusJson(
            """{"enabled":true,"verdict":{"state":"Healthy","detail":"d"},"targets":[{"id":"a","name":"X","critical":true},{"id":"b","name":"Router","state":"Down","critical":true}],"warnings":[]}""");
        Assert.Equal(TrayState.Healthy, info.State);
        Assert.Equal("Router", Assert.Single(info.Critical).Name);
    }

    [Theory]
    [InlineData("ResolverPartial", "Your resolver 10.0.0.2", "Your resolver 10.0.0.2 isn't answering (another resolver is)")]
    [InlineData("ResolverLying", "Your resolver 10.0.0.1", "Your resolver 10.0.0.1 answers with the wrong addresses")]
    [InlineData("ResolverFiltered", "Your resolver 10.0.0.1", "Your resolver 10.0.0.1 blocks the lookup names")]
    [InlineData("UpstreamResolverDown", "1.1.1.1 directly", "1.1.1.1 directly isn't answering")]
    [InlineData("NameUnresolvable", null, "A lookup name isn't resolving anywhere")]
    public void Resolver_notes_have_short_labels(string kind, string? target, string expected)
    {
        var name = target is null ? "null" : $"\"{target}\"";
        var info = TrayStatus.FromStatusJson(With(
            $"[{{\"key\":\"k\",\"kind\":\"{kind}\",\"targetName\":{name},\"message\":\"m\",\"since\":\"x\",\"acknowledged\":false}}]"));
        Assert.Equal([expected], info.Notes);
    }

    [Fact]
    public void The_verdict_name_is_kept_and_DnsDown_reads_as_name_lookups_failing_in_the_tooltip()
    {
        var info = TrayStatus.FromStatusJson(Status("Responding", verdict: "DnsDown"));
        Assert.Equal("DnsDown", info.Verdict);
        Assert.True(info.NamesFailing);
        Assert.Equal(TrayState.Down, info.State);
        Assert.StartsWith("Internet: name lookups are failing\n", TrayStatus.Tooltip(info));

        var link = TrayStatus.FromStatusJson(Status("Responding", verdict: "LinkDown"));
        Assert.False(link.NamesFailing);
        Assert.StartsWith("Internet: DOWN\n", TrayStatus.Tooltip(link));
    }

    [Fact]
    public void A_status_without_a_verdict_has_no_verdict_name() // Review Focus 5
    {
        var info = TrayStatus.FromStatusJson("""{"enabled":true,"verdict":null,"targets":[{"id":"a","name":"Router","state":"Down","scope":"Verdict","critical":false}],"pools":[]}""");
        Assert.Null(info.Verdict);
        Assert.False(info.NamesFailing);
        Assert.Equal(TrayState.Down, info.State);
        Assert.StartsWith("Internet: DOWN", TrayStatus.Tooltip(info));
    }
}
