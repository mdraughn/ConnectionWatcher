using ConEngine;
using ConEngine.Events;
using ConPersistence.Queries;
using Microsoft.AspNetCore.SignalR;

namespace ConWatch;

/// <summary>
/// Pushes engine state to SignalR clients: a statusChanged snapshot on a 1 s
/// cadence (and immediately on transitions, min 250 ms apart), plus every
/// transition as its own message. Reattached across engine rebuilds.
/// </summary>
public class StatusBroadcaster(IHubContext<StatusHub> hub, TimeProvider timeProvider,
    ILogger<StatusBroadcaster> log) : BackgroundService, IEngineObserver
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private WatcherEngine? _engine;
    private IDisposable? _subscription;
    private DateTimeOffset _lastSnapshotSent = DateTimeOffset.MinValue;

    /// <summary>Subscribes to a (re)started engine, replacing any prior subscription.</summary>
    public void Attach(WatcherEngine engine)
    {
        lock (_lock)
        {
            _subscription?.Dispose();
            _engine = engine;
            _subscription = engine.TransitionSource.Subscribe(
                t => _ = OnTransitionAsync(engine, t),
                ex => log.LogError(ex, "Transition stream error in broadcaster"));
        }
    }

    /// <summary>Pure teardown: drop the subscription and every reference to the
    /// engine. Pushing the disabled snapshot is a separate, explicit step the
    /// host takes only on the archive path — see <see cref="PushDisabledAsync"/>.
    /// Idempotent (safe to call when already detached).</summary>
    public void Detach()
    {
        lock (_lock)
        {
            _subscription?.Dispose();
            _subscription = null;
            _engine = null;
        }
    }

    /// <summary>Tells every open tab the node is now an archive: one push of the
    /// disabled snapshot. Called by the host from the archive path only, after
    /// Detach(), so a plain restart or an ordinary rebuild never flips a dashboard.
    /// Bounded at 2 s: the host awaits this while holding its rebuild lock, and a
    /// wedged hub client (or a busy send gate) must not hold that lock open —
    /// the timeout lands in the catch below and the rebuild carries on.</summary>
    public async Task PushDisabledAsync()
    {
        try
        {
            await SendStatusChangedAsync(StatusSnapshot.Disabled)
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed pushing the disabled snapshot to clients");
        }
    }

    private async Task OnTransitionAsync(WatcherEngine engine, TargetTransition t)
    {
        try
        {
            await hub.Clients.All.SendAsync("transition", TransitionDto.From(t));
            // This transition was queued before a Detach() that may since have
            // run (the subscription is disposed, not awaited); only push a
            // snapshot if this is still the attached engine, or a rebuild's
            // "disabled" push (or the next engine's own snapshot) can be
            // overtaken by this stale one.
            await SendSnapshotAsync(respectGap: true, forEngine: engine);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed pushing transition to clients");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Cadence, timeProvider, stoppingToken).ConfigureAwait(false);
                // Cadence ticks honor the same minimum spacing as transition
                // sends: a tick right after a transition-triggered snapshot
                // carries nothing new, and clients are promised MinGap.
                await SendSnapshotAsync(respectGap: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Snapshot broadcast failed");
            }
        }
    }

    /// <param name="forEngine">Set only for a transition-triggered send: the
    /// engine that transition came from. If it no longer matches the attached
    /// engine (detached or reattached since), the send is skipped rather than
    /// racing a newer push onto the wire out of order.</param>
    private async Task SendSnapshotAsync(bool respectGap, WatcherEngine? forEngine = null)
    {
        WatcherEngine? engine;
        lock (_lock)
        {
            if (forEngine is not null && !ReferenceEquals(forEngine, _engine))
                return;
            engine = _engine;
            var now = timeProvider.GetUtcNow();
            if (respectGap && now - _lastSnapshotSent < MinGap)
                return;
            _lastSnapshotSent = now;
        }
        var snapshot = StatusSnapshotBuilder.Build(engine);
        if (snapshot is null)
            return;

        // Re-check under the gate: the engine read above (and the build it
        // fed) happened outside any lock held across the wait, so a Detach()
        // (or a reattach) can land in the gap between that read and actually
        // getting the gate. Without this, a stale live snapshot can still
        // overtake — or be overtaken out of turn by — a disabled push.
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (!ReferenceEquals(_engine, engine))
                    return;
            }
            await hub.Clients.All.SendAsync("statusChanged", snapshot).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Serializes every statusChanged push behind one gate: two pushes can be
    /// dispatched concurrently (a transition-triggered send racing Detach's
    /// disabled push), and without ordering, whichever SendAsync call happens
    /// to complete first wins the wire — not whichever was dispatched first.
    /// </summary>
    private async Task SendStatusChangedAsync(StatusSnapshot snapshot)
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await hub.Clients.All.SendAsync("statusChanged", snapshot).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _sendGate.Dispose();
        base.Dispose();
    }
}
