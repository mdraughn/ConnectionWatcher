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
/// Throws from <see cref="Detach"/> exactly once, once armed. Once only: the
/// host's own cleanup calls Detach again from its finally block, and a second
/// throw there would escape the rebuild with the lock still held.
/// </summary>
internal sealed class DetachBombObserver : IEngineObserver
{
    private int _armed;
    private int _fired;

    public bool Fired => Volatile.Read(ref _fired) == 1;

    public void Arm() => Volatile.Write(ref _armed, 1);

    public void Attach(WatcherEngine engine) { }

    public void Detach()
    {
        if (Volatile.Read(ref _armed) == 1 && Interlocked.Exchange(ref _fired, 1) == 0)
            throw new InvalidOperationException("injected detach failure");
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
