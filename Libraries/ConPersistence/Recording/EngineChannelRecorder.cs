using ConEngine;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Recording;

/// <summary>
/// A <see cref="ChannelRecorder{TEvent}"/> fed straight from the engine's own streams, so the
/// host can attach it to each (re)started engine. Recorders fed by something else (the
/// model-warning ledger) stay plain <see cref="ChannelRecorder{TEvent}"/>.
/// </summary>
public abstract class EngineChannelRecorder<TEvent> : ChannelRecorder<TEvent>, IEngineObserver
{
    protected EngineChannelRecorder(ILogger log, int capacity = 1000, int batchSize = 500,
        TimeSpan? retryDelay = null)
        : base(log, capacity, batchSize, retryDelay)
    {
    }

    /// <summary>Which of the engine's streams this recorder persists.</summary>
    protected abstract IObservable<TEvent> SourceOf(WatcherEngine engine);

    public void Attach(WatcherEngine engine) => Attach(SourceOf(engine));
}
