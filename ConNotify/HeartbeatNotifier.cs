using ConPersistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConNotify;

/// <summary>
/// Dead-man's-switch heartbeat: while a Heartbeat.Url is configured, GET it
/// on an interval (default 5 minutes) to say "still alive". The receiving
/// service (healthchecks.io or similar) alerts when the pings STOP — which
/// covers the one failure mode nothing else can: this monitor itself going
/// dark. Idle until configured; reconfigures live on settings changes; the
/// first ping after (re)configuration is immediate.
/// </summary>
public class HeartbeatNotifier(SettingsStore settings, IHttpClientFactory httpFactory,
    ILogger<HeartbeatNotifier> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);
    private bool _lastPingFailed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settings.NotifierSettingsChanged += () => _wake.Release();
        var wakeTask = _wake.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var url = await settings.GetAsync(SettingKeys.HeartbeatUrl);
                // Master enable: a disabled node is deliberately off duty —
                // the check service alerting on silence is correct behavior.
                var engineEnabled = await settings.GetAsync(SettingKeys.EngineEnabled) != "false";
                if (url is not { Length: > 0 } || !engineEnabled)
                {
                    await wakeTask.ConfigureAwait(false);
                    wakeTask = _wake.WaitAsync(stoppingToken);
                    continue;
                }

                await PingAsync(url, stoppingToken).ConfigureAwait(false);

                var minutes = Math.Clamp(
                    await settings.GetIntAsync(SettingKeys.HeartbeatIntervalMinutes, 5), 1, 1440);
                var winner = await Task.WhenAny(
                    Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken), wakeTask)
                    .ConfigureAwait(false);
                if (winner == wakeTask)
                {
                    await wakeTask.ConfigureAwait(false); // surface cancellation
                    wakeTask = _wake.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Heartbeat loop error; retrying in 30s");
                try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task PingAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await httpFactory.CreateClient("webhooks").GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Heartbeat ping to {Url} returned {Status}", url, (int)response.StatusCode);
                _lastPingFailed = true;
            }
            else if (_lastPingFailed)
            {
                log.LogInformation("Heartbeat ping to {Url} recovered", url);
                _lastPingFailed = false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Expected during an internet outage — that silence IS the alert.
            if (!_lastPingFailed)
                log.LogWarning("Heartbeat ping to {Url} failed: {Message}", url, ex.GetBaseException().Message);
            _lastPingFailed = true;
        }
    }
}
