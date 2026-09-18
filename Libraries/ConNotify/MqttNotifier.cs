using System.Threading.Channels;
using ConEngine;
using ConEngine.Events;
using ConPersistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;

namespace ConNotify;

/// <summary>
/// Publishes ConnectionWatcher state to an MQTT broker with Home Assistant
/// discovery. Connects only while Mqtt.Enabled; reconfigures live on settings
/// changes; refreshes discovery + all states on every (re)connect and engine
/// rebuild. Publishes on transitions — never per ping.
/// </summary>
public class MqttNotifier(SettingsStore settings, ILogger<MqttNotifier> log) : BackgroundService
{
    /// <summary>
    /// Everything the session loop reacts to travels through ONE channel with
    /// ONE reader: transitions, a refresh request (engine (re)start), and a
    /// reconfigure request (settings changed). A second wake path (the old
    /// semaphore + WhenAny) orphaned one waiter per processed item, so pokes
    /// were swallowed and the first transition after a reconnect was lost.
    /// </summary>
    public sealed class Inbox
    {
        public sealed record RefreshMessage;
        public sealed record ReconfigureMessage;

        private readonly Channel<object> _queue = Channel.CreateBounded<object>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

        public void Post(TargetTransition transition) => _queue.Writer.TryWrite(transition);
        public void Refresh() => _queue.Writer.TryWrite(new RefreshMessage());
        public void Reconfigure() => _queue.Writer.TryWrite(new ReconfigureMessage());

        public ValueTask<object> ReadAsync(CancellationToken ct) => _queue.Reader.ReadAsync(ct);

        /// <summary>Discards everything until a reconfigure request arrives.</summary>
        public async Task WaitForReconfigureAsync(CancellationToken ct)
        {
            while (await _queue.Reader.ReadAsync(ct).ConfigureAwait(false) is not ReconfigureMessage)
            {
            }
        }
    }

    private readonly Inbox _inbox = new();
    private readonly Lock _lock = new();

    private WatcherEngine? _engine;
    private IDisposable? _subscription;
    private MqttTopics? _topics;
    private Dictionary<Guid, string> _slugsById = new();
    private HashSet<string> _publishedSlugs = new();

    /// <summary>Called by the host on every engine (re)start.</summary>
    public void Attach(WatcherEngine engine)
    {
        lock (_lock)
        {
            _subscription?.Dispose();
            _engine = engine;
            _subscription = engine.TransitionSource.Subscribe(
                _inbox.Post,
                ex => log.LogError(ex, "Transition stream error in MQTT notifier"));
        }
        _inbox.Refresh();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settings.NotifierSettingsChanged += _inbox.Reconfigure;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var enabled = await settings.GetAsync(SettingKeys.MqttEnabled) == "true";
                var host = await settings.GetAsync(SettingKeys.MqttHost);
                // Master enable: a disabled node must read offline in HA, not
                // hold stale retained states — the session end below publishes
                // availability=offline before disconnecting.
                var engineEnabled = await settings.GetAsync(SettingKeys.EngineEnabled) != "false";
                if (!enabled || !engineEnabled || host is not { Length: > 0 })
                {
                    await _inbox.WaitForReconfigureAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }
                await RunConnectedSessionAsync(host, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "MQTT session failed; retrying in 10s");
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunConnectedSessionAsync(string host, CancellationToken stoppingToken)
    {
        var port = await settings.GetIntAsync(SettingKeys.MqttPort, 1883);
        var baseTopic = await settings.GetAsync(SettingKeys.MqttBaseTopic) is { Length: > 0 } b ? b : "conwatch";
        var prefix = await settings.GetAsync(SettingKeys.MqttDiscoveryPrefix) is { Length: > 0 } p ? p : "homeassistant";
        _topics = new MqttTopics(baseTopic, prefix);

        var optionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithWillTopic(_topics.Availability)
            .WithWillPayload("offline")
            .WithWillRetain(true);
        var username = await settings.GetAsync(SettingKeys.MqttUsername);
        if (username is { Length: > 0 })
            optionsBuilder = optionsBuilder.WithCredentials(username,
                await settings.GetAsync(SettingKeys.MqttPassword) ?? "");

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        await client.ConnectAsync(optionsBuilder.Build(), stoppingToken).ConfigureAwait(false);
        log.LogInformation("MQTT connected to {Host}:{Port} (base '{Base}')", host, port, baseTopic);

        try
        {
            await PublishAsync(client, _topics.Availability, "online", retain: true, stoppingToken);
            await RefreshAllAsync(client, stoppingToken).ConfigureAwait(false);

            while (client.IsConnected && !stoppingToken.IsCancellationRequested)
            {
                var item = await _inbox.ReadAsync(stoppingToken).ConfigureAwait(false);
                switch (item)
                {
                    case Inbox.ReconfigureMessage:
                        log.LogInformation("MQTT settings changed; reconnecting");
                        return; // outer loop re-reads settings
                    case Inbox.RefreshMessage:
                        await RefreshAllAsync(client, stoppingToken).ConfigureAwait(false);
                        break;
                    case TargetTransition transition:
                        await PublishTransitionAsync(client, transition, stoppingToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await PublishAsync(client, _topics.Availability, "offline", retain: true, CancellationToken.None);
                    await client.DisconnectAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "MQTT disconnect cleanup failed");
                }
            }
        }
    }

    private async Task RefreshAllAsync(IMqttClient client, CancellationToken ct)
    {
        WatcherEngine? engine;
        lock (_lock) { engine = _engine; }
        if (engine is null || _topics is null)
            return;

        // Deterministic slugs: iterate by name.
        var slugger = new Slugger();
        var slugs = new Dictionary<Guid, string>();
        var monitors = engine.Monitors.Values.OrderBy(m => m.Target.Name).ToList();
        foreach (var monitor in monitors)
            slugs[monitor.Target.Id] = slugger.Slug(monitor.Target.Name);

        // Clear entities for targets that vanished since the last refresh.
        foreach (var stale in _publishedSlugs.Except(slugs.Values))
        {
            await PublishAsync(client, _topics.TargetDiscovery(stale), "", retain: true, ct);
            await PublishAsync(client, _topics.TargetState(stale), "", retain: true, ct);
        }

        await PublishAsync(client, _topics.VerdictDiscovery, HaDiscovery.VerdictConfig(_topics), retain: true, ct);
        await PublishAsync(client, _topics.InternetDiscovery, HaDiscovery.InternetConfig(_topics), retain: true, ct);
        foreach (var monitor in monitors)
        {
            var slug = slugs[monitor.Target.Id];
            await PublishAsync(client, _topics.TargetDiscovery(slug),
                HaDiscovery.TargetConfig(_topics, monitor.Target.Name, slug, monitor.Target.Id), retain: true, ct);
            await PublishAsync(client, _topics.TargetState(slug),
                StateMapping.TargetPayload(monitor.CurrentState), retain: true, ct);
        }

        var current = engine.Verdict?.Current;
        var verdict = current?.State.ToString() ?? "Unknown";
        await PublishAsync(client, _topics.VerdictState, verdict, retain: true, ct);
        await PublishAsync(client, _topics.VerdictAttributes,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                detail = current?.Detail,
                @operator = current?.Operator,
            }), retain: true, ct);
        await PublishAsync(client, _topics.InternetState, StateMapping.InternetPayload(verdict), retain: true, ct);

        lock (_lock)
        {
            _slugsById = slugs;
            _publishedSlugs = slugs.Values.ToHashSet();
        }
        log.LogDebug("MQTT discovery + state refresh published ({Count} targets)", monitors.Count);
    }

    private async Task PublishTransitionAsync(IMqttClient client, TargetTransition t, CancellationToken ct)
    {
        if (_topics is null)
            return;
        await PublishAsync(client, _topics.Transitions, WebhookPayload.ToJson(t), retain: false, ct);

        switch (t.Kind)
        {
            case TransitionSubjectKind.Target:
                string? slug;
                lock (_lock) { _slugsById.TryGetValue(t.TargetId, out slug); }
                if (slug is null)
                {
                    _inbox.Refresh(); // unknown target: topology changed
                    return;
                }
                await PublishAsync(client, _topics.TargetState(slug),
                    StateMapping.TargetPayload(t.To), retain: true, ct);
                break;

            case TransitionSubjectKind.Verdict:
                var verdict = t.Reason.Verdict ?? "Unknown";
                WatcherEngine? engineForOperator;
                lock (_lock) { engineForOperator = _engine; }
                await PublishAsync(client, _topics.VerdictState, verdict, retain: true, ct);
                await PublishAsync(client, _topics.VerdictAttributes,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        detail = t.Reason.Detail,
                        @operator = engineForOperator?.Verdict?.CurrentOperator,
                    }), retain: true, ct);
                await PublishAsync(client, _topics.InternetState, StateMapping.InternetPayload(verdict), retain: true, ct);
                break;
        }
    }

    private static Task PublishAsync(IMqttClient client, string topic, string payload, bool retain, CancellationToken ct) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).Build(), ct);

    public override void Dispose()
    {
        _subscription?.Dispose();
        base.Dispose();
    }
}
