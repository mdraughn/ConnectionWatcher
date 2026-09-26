using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConEngine;
using ConEngine.Events;
using ConEngine.Tracking;
using ConPersistence.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        var verdictRow = Assert.Single(rows, r => r.SubjectKind == "Verdict");
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

    [Fact]
    public async Task A_failed_transition_write_is_retried_and_lands_exactly_once()
    {
        var recorder = new TransitionRecorder(new FailingOnceDbFactory(_factory), NullLogger<TransitionRecorder>.Instance);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        source.OnNext(Transition(TransitionSubjectKind.Target, "Google", HostState.Down));

        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.FlushAsync());
        await recorder.FlushAsync(); // second attempt succeeds

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Transitions.CountAsync());
    }

    [Fact]
    public async Task A_failed_glitch_write_is_retried_and_lands_exactly_once()
    {
        var recorder = new GlitchRecorder(new FailingOnceDbFactory(_factory), NullLogger<GlitchRecorder>.Instance);
        using var source = new Subject<GlitchEvent>();
        recorder.Attach(source);
        source.OnNext(new GlitchEvent
        {
            TargetId = Guid.NewGuid(), TargetName = "Google",
            StartUtc = DateTimeOffset.UtcNow.AddSeconds(-5), EndUtc = DateTimeOffset.UtcNow,
            LostCount = 2, SampleCount = 6,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.FlushAsync());
        await recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Glitches.CountAsync());
    }

    [Fact]
    public async Task Background_loop_retries_a_failed_batch_without_waiting_for_another_event()
    {
        var recorder = new TransitionRecorder(new FailingOnceDbFactory(_factory),
            NullLogger<TransitionRecorder>.Instance, retryDelay: TimeSpan.FromMilliseconds(50));
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        await recorder.StartAsync(CancellationToken.None); // runs ExecuteAsync

        source.OnNext(Transition(TransitionSubjectKind.Target, "Google", HostState.Down));

        // One event, one failed write, no second event: the row must still land.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var check = _factory.CreateDbContext();
            if (await check.Transitions.CountAsync() == 1)
                break;
            await Task.Delay(25);
        }
        await recorder.StopAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Transitions.CountAsync());
    }

    [Fact]
    public async Task A_full_channel_drops_the_oldest_and_keeps_the_newest()
    {
        var recorder = new TransitionRecorder(_factory, NullLogger<TransitionRecorder>.Instance, capacity: 2);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        source.OnNext(Transition(TransitionSubjectKind.Target, "First", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Second", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Third", HostState.Down));

        await recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        var names = await db.Transitions.Select(t => t.SubjectName).OrderBy(n => n).ToListAsync();
        Assert.Equal(["Second", "Third"], names); // DropOldest: the newest row is what warm start seeds from
    }

    [Fact]
    public async Task A_failed_rollup_write_is_retried_next_flush()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(new FailingOnceDbFactory(_factory), time, NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: time.GetUtcNow())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushAllAsync());
        await collector.FlushAllAsync();

        await using var db = _factory.CreateDbContext();
        var row = await db.Rollups.SingleAsync(r => r.TargetId == target);
        Assert.Equal(1, row.PingCount);
    }

    [Fact]
    public async Task A_late_sample_for_a_minute_still_pending_after_a_failed_write_merges_instead_of_poisoning()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(new FailingOnceDbFactory(_factory), time, NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        var minute0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: minute0.AddSeconds(5))));
        time.Advance(TimeSpan.FromSeconds(65)); // minute 0 closed
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushClosedAsync());

        // A reply stamped inside minute 0 consumed after the tick that closed it:
        // a SECOND bucket with the same key joins the pending one.
        source.OnNext((target, new PingResult(Ip, true, 30, timestamp: minute0.AddSeconds(59))));
        time.Advance(TimeSpan.FromSeconds(60));
        await collector.FlushClosedAsync(); // used to hit the UNIQUE index forever

        await using var db = _factory.CreateDbContext();
        var row = await db.Rollups.SingleAsync(r => r.TargetId == target);
        Assert.Equal(2, row.PingCount);
        Assert.Equal(2, row.SuccessCount);
        Assert.Equal(20.0, row.RttAvg);
        Assert.Equal(30, row.RttMax);
    }

    [Fact]
    public async Task A_duplicate_pending_bucket_is_folded_once_even_across_two_failed_writes()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(new FailingNTimesDbFactory(_factory, failures: 2), time,
            NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        var minute0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: minute0.AddSeconds(5))));
        time.Advance(TimeSpan.FromSeconds(65));
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushClosedAsync()); // failure 1

        source.OnNext((target, new PingResult(Ip, true, 30, timestamp: minute0.AddSeconds(59)))); // duplicate key
        time.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushClosedAsync()); // failure 2: folded once
        await collector.FlushClosedAsync(); // succeeds

        await using var db = _factory.CreateDbContext();
        var row = await db.Rollups.SingleAsync(r => r.TargetId == target);
        Assert.Equal(2, row.PingCount);   // not 3: the duplicate was absorbed exactly once
        Assert.Equal(2, row.SuccessCount);
        Assert.Equal(20.0, row.RttAvg);
    }

    [Fact]
    public async Task Pending_buckets_are_dropped_after_too_many_consecutive_failures()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(new FailingNTimesDbFactory(_factory, failures: 2), time,
            NullLogger<RollupCollector>.Instance, maxConsecutiveFailures: 2);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: time.GetUtcNow())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushAllAsync()); // 1st failure
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushAllAsync()); // 2nd: cap hit, dropped
        await collector.FlushAllAsync(); // database works again; nothing left to write

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Rollups.CountAsync());
    }

    /// <summary>A recorder whose first write raises a cancellation that is NOT the host's.</summary>
    private sealed class ForeignCancelOnceRecorder(IDbContextFactory<ConWatchDbContext> dbFactory)
        : ChannelRecorder<TargetTransition>(NullLogger.Instance, retryDelay: TimeSpan.FromMilliseconds(50))
    {
        private int _writes;
        public int Writes => _writes;
        protected override string EventNoun => "test events";
        protected override IObservable<TargetTransition> SourceOf(WatcherEngine engine) => engine.TransitionSource;
        protected override async Task WriteBatchAsync(IReadOnlyList<TargetTransition> batch, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _writes) == 1)
                throw new OperationCanceledException("a timeout inside the write, not host stop");
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.Transitions.AddRange(batch.Select(t => new ConPersistence.Entities.TransitionRow
            {
                TimestampUtc = t.Timestamp.UtcDateTime, SubjectId = t.TargetId, SubjectName = t.TargetName,
                FromState = t.From.ToString(), ToState = t.To.ToString(), Trigger = t.Reason.Trigger.ToString(),
            }));
            await db.SaveChangesAsync(ct);
        }
    }

    [Fact]
    public async Task A_cancellation_that_is_not_host_stop_is_retried_not_fatal()
    {
        var recorder = new ForeignCancelOnceRecorder(_factory);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        await recorder.StartAsync(CancellationToken.None);
        source.OnNext(Transition(TransitionSubjectKind.Target, "Google", HostState.Down));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && recorder.Writes < 2)
            await Task.Delay(25);
        await recorder.StopAsync(CancellationToken.None);

        Assert.True(recorder.Writes >= 2, "the foreign cancellation must be retried");
        Assert.False(recorder.ExecuteTask!.IsFaulted, "the pump must not fault");
        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Transitions.CountAsync());
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class LoggedRecorder(ILogger log, int capacity) : ChannelRecorder<TargetTransition>(log, capacity)
    {
        protected override string EventNoun => "test events";
        protected override IObservable<TargetTransition> SourceOf(WatcherEngine engine) => engine.TransitionSource;
        protected override Task WriteBatchAsync(IReadOnlyList<TargetTransition> batch, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void An_evicted_event_is_logged_as_a_warning()
    {
        var log = new CapturingLogger();
        var recorder = new LoggedRecorder(log, capacity: 2);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        source.OnNext(Transition(TransitionSubjectKind.Target, "First", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Second", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Third", HostState.Down));

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("channel full", warning.Message);
        Assert.Contains("First", warning.Message);
    }
}
