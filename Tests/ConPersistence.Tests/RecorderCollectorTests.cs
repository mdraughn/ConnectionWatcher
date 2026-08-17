using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConEngine.Events;
using ConEngine.Tracking;
using ConPersistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConPersistence.Tests;

public class RecorderCollectorTests : IDisposable
{
    private static readonly IPAddress Ip = IPAddress.Parse("8.8.8.8");
    private readonly TestDbFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static TargetTransition Transition(TransitionSubjectKind kind, string name,
        HostState to, string? verdict = null) => new()
    {
        Kind = kind, TargetId = Guid.NewGuid(), TargetName = name,
        From = HostState.Responding, To = to,
        Timestamp = DateTimeOffset.UtcNow,
        Reason = new TransitionReason
        {
            Trigger = kind == TransitionSubjectKind.Verdict
                ? TransitionTrigger.VerdictChanged
                : TransitionTrigger.ThresholdCrossed,
            SuccessRate = 0.5, SampleSize = 10, Verdict = verdict,
        },
    };

    [Fact]
    public async Task Recorder_persists_transitions_with_kinds()
    {
        var recorder = new TransitionRecorder(_factory, NullLogger<TransitionRecorder>.Instance);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);

        source.OnNext(Transition(TransitionSubjectKind.Target, "Google", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Router", HostState.Faltering));
        source.OnNext(Transition(TransitionSubjectKind.Pool, "Internet", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Verdict, "Internet", HostState.Faltering, "InternetWeather"));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Quad9", HostState.Down));

        await recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        var rows = await db.Transitions.ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.Equal(3, rows.Count(r => r.SubjectKind == "Target"));
        Assert.Equal(1, rows.Count(r => r.SubjectKind == "Pool"));
        var verdictRow = Assert.Single(rows.Where(r => r.SubjectKind == "Verdict"));
        Assert.Equal("InternetWeather", verdictRow.Verdict);
        Assert.Equal("VerdictChanged", verdictRow.Trigger);
        Assert.Equal(0.5, verdictRow.SuccessRate);
    }

    [Fact]
    public async Task Collector_writes_closed_minutes_and_merges_duplicates()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var collector = new RollupCollector(_factory, time, NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var targetId = Guid.NewGuid();

        // 3 samples in minute 0, 2 in minute 1, 1 in minute 2.
        void Sample(int atSeconds, bool ok, long rtt) =>
            source.OnNext((targetId, new PingResult(Ip, ok, rtt,
                timestamp: DateTimeOffset.Parse("2026-08-16T12:00:00Z").AddSeconds(atSeconds))));

        Sample(5, true, 10); Sample(20, true, 20); Sample(50, false, 0);
        Sample(70, true, 30); Sample(80, true, 40);
        Sample(130, true, 50);

        time.Advance(TimeSpan.FromSeconds(125)); // now 12:02:05 — minutes 0 and 1 are closed
        await collector.FlushClosedAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var rows = await db.Rollups.OrderBy(r => r.PeriodStartUtc).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(3, rows[0].PingCount);
            Assert.Equal(2, rows[0].SuccessCount);
            Assert.Equal(15.0, rows[0].RttAvg);
            Assert.Equal(2, rows[1].PingCount);
        }

        // Same-period data arriving later must merge, not violate the unique index.
        Sample(55, true, 40); // belongs to already-flushed minute 0
        await collector.FlushClosedAsync();

        await using (var db = _factory.CreateDbContext())
        {
            var minute0 = await db.Rollups.SingleAsync(r =>
                r.PeriodStartUtc == DateTime.Parse("2026-08-16T12:00:00Z").ToUniversalTime());
            Assert.Equal(4, minute0.PingCount);
            Assert.Equal(3, minute0.SuccessCount);
            Assert.Equal(40, minute0.RttMax);
        }
    }
}
