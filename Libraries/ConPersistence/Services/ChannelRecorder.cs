using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Services;

/// <summary>
/// Shared shape of the event recorders: events from an engine stream flow
/// through a bounded channel (the engine never blocks on the database), a
/// background loop drains them in batches, and a batch whose write fails
/// stays pending and is retried before anything newer is read.
/// </summary>
/// <typeparam name="TEvent">The engine event being persisted.</typeparam>
public abstract class ChannelRecorder<TEvent>(ILogger log, int capacity = 1000, int batchSize = 500) : BackgroundService
{
    private readonly Channel<TEvent> _channel = Channel.CreateBounded<TEvent>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private List<TEvent>? _pending; // a batch whose write failed; retried first
    private IDisposable? _subscription;

    /// <summary>Plural noun for log lines ("transitions", "glitches").</summary>
    protected abstract string EventNoun { get; }

    /// <summary>Writes one whole batch. Throwing keeps the batch pending for the next flush.</summary>
    protected abstract Task WriteBatchAsync(IReadOnlyList<TEvent> batch, CancellationToken ct);

    /// <summary>Subscribes to an event stream, replacing any prior subscription.</summary>
    public void Attach(IObservable<TEvent> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            e =>
            {
                if (!_channel.Writer.TryWrite(e))
                    log.LogWarning("{Noun} channel full; dropped {Event}", EventNoun, e);
            },
            ex => log.LogError(ex, "{Noun} stream error", EventNoun));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Failed writing {Noun}; retrying in 5s", EventNoun);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
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
