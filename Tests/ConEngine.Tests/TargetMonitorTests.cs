using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class TargetMonitorTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("8.8.8.8");
    private static readonly PingProfile Profile = new()
    {
        Name = "Test",
        NormalInterval = TimeSpan.FromSeconds(10),
        BurstInterval = TimeSpan.FromSeconds(1),
        BurstMaxDuration = TimeSpan.FromSeconds(60),
        HistorySize = 20,
        MinUsableSample = 5,
    };

    private static (TargetMonitor monitor, List<TargetTransition> transitions, FakeTimeProvider time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var target = new Target
        {
            Id = Guid.NewGuid(), Name = "Google", Address = "8.8.8.8", ProfileName = "Test",
        };
        var monitor = new TargetMonitor(target, Profile,
            new PingDiscipline(Profile, time), time, NullLoggerFactory.Instance);
        var transitions = new List<TargetTransition>();
        monitor.TransitionSource.Subscribe(transitions.Add);
        return (monitor, transitions, time);
    }

    private static void Feed(TargetMonitor m, FakeTimeProvider time, int count, bool success)
    {
        for (int i = 0; i < count; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            m.RecordPingResult(new PingResult(Ip, success, success ? 12 : 0, timestamp: time.GetUtcNow()));
        }
    }

    [Fact]
    public void Transitions_carry_target_identity()
    {
        var (m, transitions, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 5, success: true);

        Assert.Equal(HostState.Responding, m.CurrentState);
        var t = transitions.Last();
        Assert.Equal(m.Target.Id, t.TargetId);
        Assert.Equal("Google", t.TargetName);
        Assert.Equal(HostState.Responding, t.To);
        Assert.True(t.AverageRoundtripTime > 0);
    }

    [Fact]
    public void Discipline_follows_state_changes()
    {
        var (m, _, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 5, success: true);
        Assert.Null(m.Discipline.BurstStartedAt);

        Feed(m, time, 20, success: false); // Responding -> Faltering -> Down
        Assert.Equal(HostState.Down, m.CurrentState);
        Assert.NotNull(m.Discipline.BurstStartedAt);
    }

    [Fact]
    public void Burst_loss_rate_is_attached_to_transitions_during_burst()
    {
        var (m, transitions, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 20, success: true);

        // Failures start: first transition (-> Faltering) starts the burst window.
        Feed(m, time, 20, success: false);
        var toDown = transitions.Single(t => t.To == HostState.Down);
        Assert.NotNull(toDown.Reason.BurstLossRate);
        Assert.Equal(1.0, toDown.Reason.BurstLossRate!.Value, 2);
    }

    [Fact]
    public void SetUnreachable_and_release_work_after_tracking()
    {
        var (m, transitions, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 5, success: true);
        var ancestor = Guid.NewGuid();

        m.SetUnreachable(ancestor);
        Assert.Equal(HostState.Unreachable, m.CurrentState);
        Assert.True(m.Discipline.IsParked);
        Assert.Equal(ancestor, transitions.Last().Reason.AncestorId);

        m.Release();
        Assert.Equal(HostState.Starting, m.CurrentState);
        Assert.False(m.Discipline.IsParked);
    }

    [Fact]
    public void SetUnreachable_before_tracking_starts_parked()
    {
        var (m, transitions, _) = Make();
        m.SetUnreachable(Guid.NewGuid());
        Assert.Equal(HostState.Unreachable, m.CurrentState);
        Assert.True(m.Discipline.IsParked);
        Assert.Equal(TransitionTrigger.ParentDown, transitions.Last().Reason.Trigger);

        m.Release();
        Assert.Equal(HostState.Starting, m.CurrentState);
    }

    [Fact]
    public void Two_hundred_refusals_with_no_accept_flags_persistent_refusal()
    {
        var (m, _, time) = Make();
        for (int i = 0; i < 200; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            m.RecordPingResult(new PingResult(Ip, true, 2, "Refused"));
        }
        Assert.Equal(200, m.RefusedReplies);
        Assert.Equal(0, m.AcceptedReplies);
        Assert.True(m.PersistentlyRefused);

        m.RecordPingResult(new PingResult(Ip, true, 2, "Accepted"));
        Assert.False(m.PersistentlyRefused);
    }

    [Fact]
    public void Resolution_failure_reports_once()
    {
        var (m, transitions, _) = Make();
        m.ReportResolutionFailure("DNS lookup failed for example.com");
        m.ReportResolutionFailure("DNS lookup failed for example.com");

        var t = Assert.Single(transitions);
        Assert.Equal(TransitionTrigger.ResolutionFailed, t.Reason.Trigger);
        Assert.Equal(HostState.Starting, m.CurrentState);
    }
}
