using System.Threading.Channels;
using ConEngine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Recording;

/// <summary>
/// Shared shape of the event recorders: events from an engine stream flow
/// through a bounded channel (the engine never blocks on the database), a
/// background loop drains them in batches, and a batch whose write fails
/// stays pending and is retried on a timer, not on the next event.
/// </summary>
/// <typeparam name="TEvent">The engine event being persisted.</typeparam>
public abstract class ChannelRecorder<TEvent>(ILogger log, int capacity = 1000, int batchSize = 500,
    TimeSpan? retryDelay = null) : BackgroundService, IEngineObserver
{
    // DropOldest is the right policy for transitions: the NEWEST row per subject
    // is what warm start seeds from, so under a long database outage the recent
    // past matters more than the distant. TryWrite never reports the eviction
    // (it returns false only for a completed channel), so the drop is logged here.
    private readonly Channel<TEvent> _channel = Channel.CreateBounded<TEvent>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest },
        // A field initializer cannot touch instance members (no `this` yet), so
        // the message names the event's CLR type rather than EventNoun.
        dropped => log.LogWarning("{Kind} channel full; dropped {Event}", typeof(TEvent).Name, dropped));
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    private List<TEvent>? _pending; // a batch whose write failed; retried first
    private IDisposable? _subscription;

    /// <summary>Plural noun for log lines ("transitions", "glitches").</summary>
    protected abstract string EventNoun { get; }

    /// <summary>Writes one whole batch. Throwing keeps the batch pending for the next flush.</summary>
    protected abstract Task WriteBatchAsync(IReadOnlyList<TEvent> batch, CancellationToken ct);

    /// <summary>Which of the engine's streams this recorder persists.</summary>
    protected abstract IObservable<TEvent> SourceOf(WatcherEngine engine);

    /// <summary>Subscribes to an event stream, replacing any prior subscription.</summary>
    public void Attach(IObservable<TEvent> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            e => _channel.Writer.TryWrite(e),
            ex => log.LogError(ex, "{Noun} stream error", EventNoun));
    }

    public void Attach(WatcherEngine engine) => Attach(SourceOf(engine));

    public void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                // With a batch still pending, do not wait for a NEW event: the
                // retry is due on the timer, not on the next transition (which
                // on a quiet link can be hours away).
                if (_pending is null && !await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                    break;
                try
                {
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Failed writing {Noun}; retrying in {Delay}", EventNoun, _retryDelay);
                    await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: drain what's left without a token.
            try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Final {Noun} flush failed", EventNoun); }
        }
    }

    /// <summary>
    /// Drains the channel into the database in batches. Serialized: the
    /// background loop and a caller-driven flush must not split one batch
    /// between them. A batch is only released once its write has succeeded.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var batch = _pending ?? new List<TEvent>();
                while (batch.Count < batchSize && _channel.Reader.TryRead(out var e))
                    batch.Add(e);
                if (batch.Count == 0)
                    return;

                _pending = batch;
                await WriteBatchAsync(batch, ct).ConfigureAwait(false);
                _pending = null;
                log.LogDebug("Recorded {Count} {Noun}", batch.Count, EventNoun);

                if (batch.Count < batchSize)
                    return; // the channel is empty; a full batch means there may be more
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
