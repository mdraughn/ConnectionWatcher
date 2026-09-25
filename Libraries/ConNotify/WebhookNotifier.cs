using System.Text;
using System.Threading.Channels;
using ConEngine;
using ConEngine.Events;
using ConPersistence;
using ConPersistence.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConNotify;

/// <summary>
/// POSTs each transition as JSON to every configured webhook URL. Settings are
/// read per event (transitions are rare), so changes apply immediately. A
/// failed delivery is logged and dropped — webhooks are fire-and-forget.
/// </summary>
public class WebhookNotifier(SettingsStore settings, IHttpClientFactory httpFactory, ILogger<WebhookNotifier> log)
    : BackgroundService, IEngineObserver
{
    private readonly Channel<TargetTransition> _queue = Channel.CreateBounded<TargetTransition>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
    private IDisposable? _subscription;

    /// <summary>Called by the host on every engine (re)start.</summary>
    public void Attach(WatcherEngine engine)
    {
        _subscription?.Dispose();
        _subscription = engine.TransitionSource.Subscribe(
            t => _queue.Writer.TryWrite(t),
            ex => log.LogError(ex, "Transition stream error in webhook notifier"));
    }

    public void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var transition in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await DeliverAsync(transition, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Webhook delivery failed for transition {Id}", transition.TargetId);
            }
        }
    }

    private async Task DeliverAsync(TargetTransition transition, CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.WebhookEnabled) != "true")
            return;
        if (await settings.GetAsync(SettingKeys.WebhookVerdictOnly) == "true" &&
            transition.Kind != TransitionSubjectKind.Verdict)
            return;

        var urls = (await settings.GetAsync(SettingKeys.WebhookUrls) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (urls.Length == 0)
            return;

        var json = WebhookPayload.ToJson(transition);
        var client = httpFactory.CreateClient("webhooks");
        foreach (var url in urls)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(url, content, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    log.LogWarning("Webhook {Url} returned {Status}",
                        LogSafe.Url(url), (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogWarning("Webhook {Url} failed: {Message}", LogSafe.Url(url), ex.Message);
            }
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
