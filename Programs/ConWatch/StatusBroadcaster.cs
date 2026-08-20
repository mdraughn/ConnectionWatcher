using ConEngine;
using ConEngine.Events;
using Microsoft.AspNetCore.SignalR;

namespace ConWatch;

/// <summary>
/// Pushes engine state to SignalR clients: a statusChanged snapshot on a 1 s
/// cadence (and immediately on transitions, min 250 ms apart), plus every
/// transition as its own message. Reattached across engine rebuilds.
/// </summary>
public class StatusBroadcaster(IHubContext<StatusHub> hub, TimeProvider timeProvider,
    ILogger<StatusBroadcaster> log) : BackgroundService
{
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(250);

    private readonly Lock _lock = new();
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
                t => _ = OnTransitionAsync(t),
                ex => log.LogError(ex, "Transition stream error in broadcaster"));
        }
    }

    private async Task OnTransitionAsync(TargetTransition t)
    {
        try
        {
            await hub.Clients.All.SendAsync("transition", new
            {
                Timestamp = t.Timestamp,
                SubjectId = t.TargetId,
                SubjectName = t.TargetName,
                SubjectKind = t.Kind.ToString(),
                From = t.From.ToString(),
                To = t.To.ToString(),
                Trigger = t.Reason.Trigger.ToString(),
                Detail = t.Reason.Detail,
                Verdict = t.Reason.Verdict,
                SuccessRate = t.Reason.SuccessRate,
                SampleSize = t.Reason.SampleSize,
                BurstLossRate = t.Reason.BurstLossRate,
            });
            await SendSnapshotAsync(respectGap: true);
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
                await SendSnapshotAsync(respectGap: false).ConfigureAwait(false);
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

    private async Task SendSnapshotAsync(bool respectGap)
    {
        WatcherEngine? engine;
        lock (_lock)
        {
            engine = _engine;
            var now = timeProvider.GetUtcNow();
            if (respectGap && now - _lastSnapshotSent < MinGap)
                return;
            _lastSnapshotSent = now;
        }
        var snapshot = StatusSnapshotBuilder.Build(engine);
        if (snapshot is null)
            return;
        await hub.Clients.All.SendAsync("statusChanged", snapshot).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
