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
}
