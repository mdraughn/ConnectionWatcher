
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>One target's samples in one window.</summary>
public sealed record WindowCount(int Samples, int Lost)
{
    public double Loss => Samples == 0 ? 0 : (double)Lost / Samples;
    /// <summary>Enough samples to compare with other targets in the same window.</summary>
    public bool Usable => Samples >= LossWindows.MinSamples;
    /// <summary>Got at least one reply in the window.</summary>
    public bool Answered => Lost < Samples;
}

/// <summary>Every target's samples over the same time span.</summary>
public sealed record LossWindow(DateTimeOffset Start, IReadOnlyDictionary<Guid, WindowCount> Targets)
{
    public DateTimeOffset End => Start + LossWindows.Span;
}

/// <summary>
/// Buckets verdict-scope ping samples into clock-aligned windows so losses of
/// different targets can be compared over the same span (spec 2026-10-09
/// §4.5). Bursts land in the same windows and only add samples. A window
/// closes once a sample arrives past its end plus <see cref="CloseDelay"/>
/// (controllers run on separate threads; a little lateness is normal); a
/// sample for an already-closed window is dropped. Closed windows are
/// published outside the lock, in order.
/// </summary>
public sealed class LossWindows
{
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(5);
    public const int MinSamples = 5;
    public const int Keep = 30;

    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly SortedDictionary<DateTimeOffset, Dictionary<Guid, WindowCount>> _open = new();
    private readonly List<LossWindow> _closed = new();
    private readonly ConcurrentQueue<LossWindow> _outbox = new();
    private readonly Subject<LossWindow> _windowClosed = new();
    private readonly ILogger? _log;
    private DateTimeOffset? _closedThrough;

    public LossWindows(ILogger? log = null) => _log = log;

    public static DateTimeOffset WindowStart(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % Span.Ticks, TimeSpan.Zero);

    /// <summary>A window closed. Published outside the collector's lock.</summary>
    public IObservable<LossWindow> WindowClosed => new IsolatedObservable(this);

    /// <summary>A Subject stops at its first throwing observer, and Rx's
    /// auto-detaching observers (Observable.Create) unsubscribe a thrower. Here a
    /// subscriber that throws is logged and stays subscribed.</summary>
    private sealed class IsolatedObservable(LossWindows owner) : IObservable<LossWindow>
    {
        public IDisposable Subscribe(IObserver<LossWindow> observer) =>
            owner._windowClosed.Subscribe(
                window =>
                {
                    try { observer.OnNext(window); }
                    catch (Exception ex)
                    {
                        owner._log?.LogWarning(ex,
                            "A loss-window subscriber threw; it stays subscribed. Window {Start} is still recorded.", window.Start);
                    }
                },
                observer.OnError,
                observer.OnCompleted);
    }

    /// <summary>Closed windows, oldest first, at most <see cref="Keep"/>.</summary>
    public IReadOnlyList<LossWindow> Closed
    {
        get { lock (_lock) { return _closed.ToList(); } }
    }

    /// <summary>The newest closed window if it ended no more than two spans before <paramref name="now"/>.</summary>
    public LossWindow? LatestFresh(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _closed.Count > 0 && now - _closed[^1].End <= Span * 2 ? _closed[^1] : null;
        }
    }

    /// <summary>This target's count in the newest open window, or null.</summary>
    public WindowCount? Open(Guid targetId)
    {
        lock (_lock)
        {
            return _open.Count > 0 && _open.Last().Value.TryGetValue(targetId, out var count) ? count : null;
        }
    }

    public void Record(Guid targetId, DateTimeOffset at, bool success)
    {
        lock (_lock)
        {
            var start = WindowStart(at);
            if (_closedThrough is { } through && start < through)
                return; // late: that window is already closed
            if (!_open.TryGetValue(start, out var counts))
                _open[start] = counts = new Dictionary<Guid, WindowCount>();
            var prior = counts.GetValueOrDefault(targetId) ?? new WindowCount(0, 0);
            counts[targetId] = new WindowCount(prior.Samples + 1, prior.Lost + (success ? 0 : 1));

            // Per-ping hot path: build the due list only when the oldest open window is due.
            if (_open.Keys.First() + Span + CloseDelay <= at)
            {
                foreach (var due in _open.Keys.Where(s => s + Span + CloseDelay <= at).ToList())
                {
                    var window = new LossWindow(due, _open[due]);
                    _open.Remove(due);
                    _closed.Add(window);
                    if (_closed.Count > Keep)
                        _closed.RemoveAt(0);
                    _closedThrough = window.End;
                    _outbox.Enqueue(window);
                }
            }
        }
        Drain();
    }

    /// <summary>Same discipline as the coordinators: TryEnter, so a thread that
    /// finds another draining leaves its items to that thread.</summary>
    private void Drain()
    {
        while (!_outbox.IsEmpty)
        {
            if (!_publishLock.TryEnter())
                return;
            try
            {
                while (_outbox.TryDequeue(out var window))
                    _windowClosed.OnNext(window);
            }
            finally
            {
                _publishLock.Exit();
            }
        }
    }
}
