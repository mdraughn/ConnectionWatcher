using ConEngine;
using Microsoft.Extensions.Logging;

namespace ConWatch.Tests;

/// <summary>
/// Throws from <see cref="Attach"/> the next N times, once armed: the
/// rebuild that attaches observers fails like any transient start error.
/// </summary>
internal sealed class AttachBombObserver : IEngineObserver
{
    private int _remaining;
    private int _fired;

    /// <summary>How many attaches have thrown so far.</summary>
    public int Fired => Volatile.Read(ref _fired);

    public void Arm(int times) => Volatile.Write(ref _remaining, times);

    public void Attach(WatcherEngine engine)
    {
        if (Interlocked.Decrement(ref _remaining) >= 0)
        {
            Interlocked.Increment(ref _fired);
            throw new InvalidOperationException("injected rebuild failure");
        }
        Interlocked.Exchange(ref _remaining, 0);
    }

    public void Detach() { }
}

/// <summary>
/// A transition recorder whose database write fails once armed. The archive
/// path flushes the recorders, so this fails an archive rebuild (a throwing
/// observer Detach no longer can: the host logs it and carries on). Arm
/// queues one event, so there is a batch to write and the flush really throws.
/// </summary>
internal sealed class FlushBombRecorder(
    Microsoft.EntityFrameworkCore.IDbContextFactory<ConPersistence.ConWatchDbContext> dbFactory,
    ILogger<ConPersistence.Recording.TransitionRecorder> log)
    : ConPersistence.Recording.TransitionRecorder(dbFactory, log)
{
    private int _armed;
    private int _fired;

    /// <summary>How many batch writes have thrown so far.</summary>
    public int Fired => Volatile.Read(ref _fired);

    public void Arm()
    {
        Volatile.Write(ref _armed, 1);
        Attach(System.Reactive.Linq.Observable.Return(new ConEngine.Events.TargetTransition
        {
            TargetId = Guid.NewGuid(),
            TargetName = "bomb",
            From = default,
            To = default,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = new ConEngine.Events.TransitionReason { Trigger = default },
        }));
    }

    protected override Task WriteBatchAsync(IReadOnlyList<ConEngine.Events.TargetTransition> batch, CancellationToken ct)
    {
        if (Volatile.Read(ref _armed) == 1)
        {
            Interlocked.Increment(ref _fired);
            throw new InvalidOperationException("injected flush failure");
        }
        return base.WriteBatchAsync(batch, ct);
    }
}

/// <summary>Records every log entry (all levels) so a test can read what the host said and when.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public sealed record Entry(string Category, LogLevel Level, string Message);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Snapshot()
    {
        lock (_entries)
            return [.. _entries];
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

    public void Dispose() { }

    private sealed class Capture(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (owner._entries)
                owner._entries.Add(new Entry(category, logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// Throws from <see cref="Detach"/> on EVERY call once armed, and counts the
/// calls: it throws from the host's own cleanup too, which is what exposes a
/// lock released after it.
/// </summary>
internal sealed class AlwaysThrowDetachObserver : IEngineObserver
{
    private int _armed;
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Attach(WatcherEngine engine) { }

    public void Detach()
    {
        Interlocked.Increment(ref _calls);
        if (Volatile.Read(ref _armed) == 1)
            throw new InvalidOperationException("injected detach failure");
    }
}

/// <summary>Counts <see cref="Detach"/> calls and never throws.</summary>
internal sealed class DetachCountingObserver : IEngineObserver
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public void Attach(WatcherEngine engine) { }

    public void Detach() => Interlocked.Increment(ref _calls);
}
