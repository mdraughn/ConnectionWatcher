using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConPersistence.Entities;
using ConPersistence.Queries;
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

    [Fact]
    public void Only_engine_fed_recorders_are_engine_observers()
    {
        Assert.True(typeof(IEngineObserver).IsAssignableFrom(typeof(TransitionRecorder)));
        Assert.True(typeof(IEngineObserver).IsAssignableFrom(typeof(GlitchRecorder)));
        Assert.False(typeof(IEngineObserver).IsAssignableFrom(typeof(ConPersistence.Warnings.ModelWarningRecorder)));
    }

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
    public async Task Samples_taken_while_the_target_shows_unreachable_are_not_rolled_up()
    {
        // Final review I3 / spec §4.4: explained time carries no data and no
        // blame — an own-router outage must not count against the anchors.
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var profile = new PingProfile { Name = "Test", HistorySize = 20, MinUsableSample = 5 };
        var target = new Target { Id = Guid.NewGuid(), Name = "Google", Address = "8.8.8.8", ProfileName = "Test" };
        var monitor = new TargetMonitor(target, profile, new PingDiscipline(profile, time), time, NullLoggerFactory.Instance);
        monitor.StartTracking(Ip);
        void Drive(int count, bool ok)
        {
            for (int i = 0; i < count; i++)
                monitor.RecordPingResult(new PingResult(Ip, ok, ok ? 10 : 0, timestamp: time.GetUtcNow()));
        }

        var collector = new RollupCollector(_factory, time, NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source, new Dictionary<Guid, TargetMonitor> { [target.Id] = monitor });
        void Sample(int atSeconds, bool ok) =>
            source.OnNext((target.Id, new PingResult(Ip, ok, ok ? 10 : 0,
                timestamp: DateTimeOffset.Parse("2026-08-16T12:00:00Z").AddSeconds(atSeconds))));

        Drive(5, true);
        Assert.Equal(HostState.Responding, monitor.CurrentState);
        Sample(5, true);                         // minute 0: counted

        monitor.SetExplanation(Guid.NewGuid());  // the router died…
        Drive(20, false);                        // …and this target proves itself down
        Assert.Equal(HostState.Unreachable, monitor.CurrentState);
        Sample(65, false); Sample(70, false);    // minute 1: explained, not rolled up

        monitor.SetExplanation(null);            // the router recovered: released
        Sample(130, true);                       // minute 2: counted again

        await collector.FlushAllAsync();
        await using var db = _factory.CreateDbContext();
        var rows = await db.Rollups.Where(r => r.TargetId == target.Id).OrderBy(r => r.PeriodStartUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(DateTime.Parse("2026-08-16T12:00:00Z").ToUniversalTime(), rows[0].PeriodStartUtc);
        Assert.Equal(1, rows[0].PingCount);
        Assert.Equal(DateTime.Parse("2026-08-16T12:02:00Z").ToUniversalTime(), rows[1].PeriodStartUtc);
        Assert.Equal(1, rows[1].PingCount);
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
        // The test polls while the loop writes: two connections, so not the shared in-memory one.
        using var factory = new FileDbFactory();
        await factory.PrepareAsync();
        var recorder = new TransitionRecorder(new FailingOnceDbFactory(factory),
            NullLogger<TransitionRecorder>.Instance, retryDelay: TimeSpan.FromMilliseconds(50));
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        await recorder.StartAsync(CancellationToken.None); // runs ExecuteAsync

        source.OnNext(Transition(TransitionSubjectKind.Target, "Google", HostState.Down));

        // One event, one failed write, no second event: the row must still land.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var check = factory.CreateDbContext();
            if (await check.Transitions.CountAsync() == 1)
                break;
            await Task.Delay(25);
        }
        await recorder.StopAsync(CancellationToken.None);

        await using var db = factory.CreateDbContext();
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

    /// <summary>Fails every context request while <see cref="Failing"/>, counting the attempts.</summary>
    private sealed class SwitchableFactory(IDbContextFactory<ConWatchDbContext> inner) : IDbContextFactory<ConWatchDbContext>
    {
        private int _failed;
        public volatile bool Failing = true;
        public int FailedAttempts => Volatile.Read(ref _failed);

        public ConWatchDbContext CreateDbContext()
        {
            if (!Failing)
                return inner.CreateDbContext();
            Interlocked.Increment(ref _failed);
            throw new InvalidOperationException("simulated SQLITE_BUSY");
        }
    }

    [Fact]
    public async Task Stopping_drains_queued_and_failed_batches()
    {
        var factory = new SwitchableFactory(_factory);
        // A retry delay far beyond the test: the loop sits in its retry wait until stopped.
        var recorder = new TransitionRecorder(factory, NullLogger<TransitionRecorder>.Instance,
            retryDelay: TimeSpan.FromHours(1));
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        await recorder.StartAsync(CancellationToken.None);

        source.OnNext(Transition(TransitionSubjectKind.Target, "Failed", HostState.Down));
        await TestWait.Eventually(() => factory.FailedAttempts >= 1, what: "the first write was never attempted");
        source.OnNext(Transition(TransitionSubjectKind.Target, "Queued", HostState.Down));
        factory.Failing = false;
        await recorder.StopAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        var names = await db.Transitions.Select(t => t.SubjectName).OrderBy(n => n).ToListAsync();
        Assert.Equal(["Failed", "Queued"], names);
    }

    [Fact]
    public async Task Bursts_larger_than_a_batch_all_land()
    {
        // The default batch is 500: 1,200 queued events take three writes from one flush.
        var recorder = new TransitionRecorder(_factory, NullLogger<TransitionRecorder>.Instance, capacity: 2000);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        for (var i = 0; i < 1200; i++)
            source.OnNext(Transition(TransitionSubjectKind.Target, $"T{i}", HostState.Down));

        await recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1200, await db.Transitions.CountAsync());
    }

    /// <summary>Advances fake time one 15 s loop tick at a time, waiting for the woken loop to
    /// arm its next timer, until the target's minute row exists. Returns the ticks it took.</summary>
    private async Task<int> TicksUntilRolledUpAsync(TimerCountingTime time, Guid target)
    {
        for (var tick = 1; tick <= 10; tick++)
        {
            var before = time.TimersCreated;
            time.Advance(TimeSpan.FromSeconds(15));
            await time.NewTimerArmedSince(before);
            await using var db = _factory.CreateDbContext();
            if (await db.Rollups.AnyAsync(r => r.TargetId == target))
                return tick;
        }
        throw new Xunit.Sdk.XunitException("the collector loop never wrote the closed minute");
    }

    [Fact]
    public async Task The_collector_loop_writes_closed_minutes()
    {
        var time = new TimerCountingTime();
        time.SetUtcNow(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(_factory, time, NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        await collector.StartAsync(CancellationToken.None);
        await time.WaitForTimersAsync(1);

        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: time.GetUtcNow().AddSeconds(5))));
        var ticks = await TicksUntilRolledUpAsync(time, target);

        // Minute 00:00 closes at 00:01:00, the fourth 15 s tick; no flush before that.
        Assert.Equal(4, ticks);
        await using (var db = _factory.CreateDbContext())
            Assert.Equal(1, (await db.Rollups.SingleAsync(r => r.TargetId == target)).PingCount);
        await collector.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_collector_loop_writes_a_failed_minute_on_the_next_tick()
    {
        var time = new TimerCountingTime();
        time.SetUtcNow(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(new FailingOnceDbFactory(_factory), time,
            NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        await collector.StartAsync(CancellationToken.None);
        await time.WaitForTimersAsync(1);

        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: time.GetUtcNow().AddSeconds(5))));
        var ticks = await TicksUntilRolledUpAsync(time, target);

        Assert.Equal(5, ticks); // the 4th tick's write failed; the loop survived and the 5th landed it
        await using (var db = _factory.CreateDbContext())
            Assert.Equal(1, await db.Rollups.CountAsync(r => r.TargetId == target));
        await collector.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_recorded_transition_reads_back_as_its_live_dto()
    {
        var evt = new TargetTransition
        {
            Kind = TransitionSubjectKind.Verdict, TargetId = Guid.NewGuid(), TargetName = "Internet",
            From = HostState.Responding, To = HostState.Faltering,
            Timestamp = new DateTimeOffset(2026, 9, 18, 10, 0, 0, 123, TimeSpan.FromHours(-7)),
            Reason = new TransitionReason
            {
                Trigger = TransitionTrigger.VerdictChanged, SuccessRate = 0.8125, SampleSize = 40,
                BurstLossRate = 0.25, AncestorId = Guid.NewGuid(), Detail = "2 of 3 anchors degraded",
                Verdict = "DnsDown",
                Dns = new DnsBlame(DnsBlameKind.YourResolver, "resolver silent", ["192.0.2.53"]),
            },
            AverageRoundtripTime = 12.5f,
        };
        var recorder = new TransitionRecorder(_factory, NullLogger<TransitionRecorder>.Instance);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        source.OnNext(evt);
        await recorder.FlushAsync();

        TransitionRow row;
        await using (var db = _factory.CreateDbContext())
            row = await db.Transitions.AsNoTracking().SingleAsync();
        var recorded = TransitionDto.From(row);
        var live = TransitionDto.From(evt);

        Assert.NotNull(recorded.Id);
        Assert.Equal(TimeSpan.Zero, recorded.Timestamp.Offset);
        Assert.Equal(TimeSpan.Zero, live.Timestamp.Offset);
        // History rows keep neither the row id on live messages nor the DNS blame on rows.
        Assert.Equal(live with { Dns = null }, recorded with { Id = null });
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

    [Fact]
    public void Many_drops_log_one_channel_full_warning_not_one_per_drop()
    {
        var log = new CapturingLogger();
        var recorder = new LoggedRecorder(log, capacity: 2);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        for (var i = 0; i < 10; i++)
            source.OnNext(Transition(TransitionSubjectKind.Target, $"T{i}", HostState.Down));

        lock (log.Entries)
            Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("channel full"));
    }
}
