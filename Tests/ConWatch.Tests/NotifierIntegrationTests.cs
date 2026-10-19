using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;

namespace ConWatch.Tests;

/// <summary>
/// End-to-end notifier tests through the full app: fake pings drive real
/// transitions, which must reach a webhook listener / a real MQTT broker.
/// The MQTT test runs Mosquitto in Docker and skips itself when Docker is
/// unavailable.
///
/// Every listener serves ONE test-specific path prefix and 404s the rest.
/// FreePort can hand a test the port a previous test just released, and the
/// previous test's app may still be delivering fire-and-forget webhooks to
/// it; without the prefix those strays were counted (or asserted on) as
/// this test's traffic — the flake that kept this class quarantined.
/// </summary>
public class NotifierIntegrationTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A loopback HTTP listener that records requests under its own prefix only.</summary>
    private sealed class Hook : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _pump;
        private readonly List<(string Path, string Body)> _received = new();
        // Set before Stop(), which aborts the pending GetContextAsync before IsListening
        // turns false: asking the listener lost that race and faulted the pump.
        private volatile bool _stopping;

        public int Port { get; }
        public string Prefix { get; } = "/t-" + Guid.NewGuid().ToString("N")[..8];
        public string Url(string path) => $"http://127.0.0.1:{Port}{Prefix}{path}";

        public Hook()
        {
            Port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            while (!_stopping)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) when (_stopping) { return; }
                try
                {
                    var path = ctx.Request.Url!.AbsolutePath;
                    if (!path.StartsWith(Prefix, StringComparison.Ordinal))
                    {
                        // A stray from another test's app on a reused port.
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    lock (_received) _received.Add((path[Prefix.Length..], body));
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                }
                catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
                {
                    // The app's webhook client (10s timeout, fire-and-forget) can
                    // abort a connection mid-read under load; drop that request
                    // and keep serving. If Dispose stopped us mid-request, the
                    // listener is gone and Abort itself throws: just end.
                    if (_stopping)
                        return;
                    try { ctx.Response.Abort(); }
                    catch (ObjectDisposedException) { return; }
                }
            }
        }

        public List<(string Path, string Body)> Received
        {
            get { lock (_received) return _received.ToList(); }
        }

        public void Dispose()
        {
            _stopping = true;
            _listener.Stop();
            _pump.Wait(TimeSpan.FromSeconds(5));
            _listener.Close();
        }
    }

    [Fact]
    public async Task Hook_disposes_cleanly_while_a_request_is_mid_body()
    {
        // The app's fire-and-forget webhooks can still be arriving when a test
        // ends; stopping the listener then must not fault the pump (it used to,
        // via Response.Abort() on the disposed listener, failing a passed test).
        var hook = new Hook();
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, hook.Port);
        var head = $"POST {new Uri(hook.Url("/hook")).AbsolutePath} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\npartial";
        await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
        await Task.Delay(300); // let the pump start reading the body it will never finish

        hook.Dispose();
    }

    [Fact]
    public void Hook_disposes_cleanly_while_waiting_for_a_request()
    {
        // Stop() aborts the pending GetContextAsync before IsListening turns false, so a
        // pump that asked the listener whether it was stopping could fault and fail
        // whichever test's Dispose it was. Every dispose is a chance at that race.
        for (var i = 0; i < 200; i++)
            new Hook().Dispose();
    }

    [Fact]
    public async Task Transitions_are_delivered_to_a_webhook()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        var put = await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.Urls"] = hook.Url("/hook"),
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        await ConfigApiTests.SeedHomeAsync(client);
        await ConfigApiTests.Eventually(() => Task.FromResult(hook.Received.Count > 0));

        var first = hook.Received[0];
        Assert.Equal("/hook", first.Path);
        var payload = JsonSerializer.Deserialize<JsonElement>(first.Body);
        Assert.True(payload.TryGetProperty("to", out _));
        Assert.True(payload.TryGetProperty("subjectKind", out _));
    }

    [Fact]
    public async Task Local_target_transitions_are_not_delivered_to_a_webhook()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.Urls"] = hook.Url("/hook"),
        });

        await ConfigApiTests.SeedHomeAsync(client);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "LocalNas", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        // A verdict-scope target added AFTER the Local one is a marker: webhooks are
        // delivered FIFO, so once it arrives any Local delivery would already have.
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "MarkerHost", address = "203.0.113.20", parent = "Router", profile = "Fast",
        })).EnsureSuccessStatusCode();

        await ConfigApiTests.Eventually(() =>
            Task.FromResult(hook.Received.Any(r => r.Body.Contains("MarkerHost"))));

        Assert.DoesNotContain(hook.Received, r => r.Body.Contains("LocalNas"));
    }

    [Fact]
    public async Task VerdictOnly_suppresses_target_transitions()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.VerdictOnly"] = "true",
            ["Webhook.Urls"] = hook.Url("/hook"),
        });

        // SeedHome creates an anchor pool, so the verdict transitions to Healthy;
        // the target transitions that precede it must never arrive.
        await ConfigApiTests.SeedHomeAsync(client);
        await ConfigApiTests.Eventually(() => Task.FromResult(hook.Received.Count > 0));
        await Task.Delay(500); // anything wrongly queued behind the verdict would land by now

        var kinds = hook.Received.Select(r =>
            JsonSerializer.Deserialize<JsonElement>(r.Body).GetProperty("subjectKind").GetString()).ToList();
        Assert.NotEmpty(kinds);
        Assert.All(kinds, kind => Assert.Equal("Verdict", kind));
    }

    [Fact]
    public async Task A_critical_targets_failure_reaches_a_verdict_only_webhook()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.VerdictOnly"] = "true",
            ["Webhook.Urls"] = hook.Url("/hook"),
        });
        await ConfigApiTests.SeedHomeAsync(client);
        // A critical LAN device (Local scope, never a verdict subject) that is down from the start.
        factory.Pings.SetUp(IPAddress.Parse("203.0.113.9"), false);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "CriticalNas", address = "203.0.113.9", profile = "Fast", scope = "Local", critical = true,
        })).EnsureSuccessStatusCode();

        await ConfigApiTests.Eventually(() => Task.FromResult(hook.Received.Any(r =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(r.Body);
            return body.GetProperty("subjectName").GetString() == "CriticalNas"
                && body.GetProperty("to").GetString() == "Down";
        })));
        // Only its failure got through verdict-only: no Starting/Initializing noise for it.
        Assert.All(hook.Received.Where(r => r.Body.Contains("CriticalNas")), r =>
            Assert.Equal("Down", JsonSerializer.Deserialize<JsonElement>(r.Body).GetProperty("to").GetString()));
    }

    [Fact]
    public async Task Heartbeat_pings_immediately_when_configured_and_follows_url_changes()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();

        // Configuring the URL wakes the notifier; the first ping is immediate.
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Heartbeat.Url"] = hook.Url("/check-a"),
        });
        await ConfigApiTests.Eventually(() => Task.FromResult(hook.Received.Count > 0));
        Assert.Equal("/check-a", hook.Received[0].Path);

        // A URL change re-pings the new target without waiting out the interval.
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Heartbeat.Url"] = hook.Url("/check-b"),
        });
        await ConfigApiTests.Eventually(() =>
            Task.FromResult(hook.Received.Any(r => r.Path == "/check-b")));
    }

    // ---------------- MQTT (Mosquitto in Docker) ----------------

    private static bool DockerAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var proc = Process.Start(psi)!;
            return proc.WaitForExit(8000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // Starts a Mosquitto container. Where Docker or the container is unavailable the test
    // skips locally, but fails in CI (CI=true) so a broken runner can't pass silently.
    private static (int BrokerPort, string ContainerId) RequireMosquitto()
    {
        string containerId = "";
        int brokerPort = 0;
        if (DockerAvailable())
        {
            brokerPort = FreePort();
            containerId = RunDocker(
                $"run -d --rm -p {brokerPort}:1883 eclipse-mosquitto:2 mosquitto -c /mosquitto-no-auth.conf").Trim();
        }
        if (containerId.Length == 0)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                Assert.Fail("Docker/Mosquitto unavailable in CI");
            Skip.If(true, "Docker/Mosquitto unavailable");
        }
        return (brokerPort, containerId);
    }

    [SkippableFact]
    public async Task Mqtt_publishes_availability_discovery_and_states_to_a_real_broker()
    {
        var (brokerPort, containerId) = RequireMosquitto();
        try
        {
            await WaitForBrokerAsync(brokerPort);

            // Independent subscriber collecting everything the app publishes.
            var messages = new Dictionary<string, string>();
            using var subscriber = new MqttClientFactory().CreateMqttClient();
            subscriber.ApplicationMessageReceivedAsync += e =>
            {
                lock (messages)
                    messages[e.ApplicationMessage.Topic] = e.ApplicationMessage.ConvertPayloadToString() ?? "";
                return Task.CompletedTask;
            };
            await subscriber.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort).Build());
            await subscriber.SubscribeAsync("conwatch/#");
            await subscriber.SubscribeAsync("homeassistant/#");

            using var factory = new ConWatchFactory();
            var client = factory.CreateClient();
            await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
            {
                ["Mqtt.Enabled"] = "true",
                ["Mqtt.Host"] = "127.0.0.1",
                ["Mqtt.Port"] = brokerPort.ToString(),
            });
            await ConfigApiTests.SeedHomeAsync(client);

            await ConfigApiTests.Eventually(() =>
            {
                lock (messages)
                    return Task.FromResult(
                        messages.GetValueOrDefault("conwatch/availability") == "online" &&
                        messages.Keys.Any(k => k.StartsWith("homeassistant/binary_sensor/")) &&
                        messages.Any(m => m.Key.StartsWith("conwatch/target/") && m.Value == "ON"));
            });

            lock (messages)
            {
                var discovery = messages.First(m => m.Key.StartsWith("homeassistant/binary_sensor/") &&
                                                    m.Key.EndsWith("/config"));
                var config = JsonSerializer.Deserialize<JsonElement>(discovery.Value);
                Assert.Equal("conwatch/availability",
                    config.GetProperty("availability_topic").GetString());
                Assert.Equal("ConnectionWatcher",
                    config.GetProperty("device").GetProperty("name").GetString());
            }
        }
        finally
        {
            RunDocker($"rm -f {containerId}");
        }
    }

    [SkippableFact]
    public async Task Mqtt_settings_change_after_traffic_reconnects_on_the_new_base_topic()
    {
        var (brokerPort, containerId) = RequireMosquitto();
        try
        {
            await WaitForBrokerAsync(brokerPort);

            var messages = new Dictionary<string, string>();
            using var subscriber = new MqttClientFactory().CreateMqttClient();
            subscriber.ApplicationMessageReceivedAsync += e =>
            {
                lock (messages)
                    messages[e.ApplicationMessage.Topic] = e.ApplicationMessage.ConvertPayloadToString() ?? "";
                return Task.CompletedTask;
            };
            await subscriber.ConnectAsync(new MqttClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", brokerPort).Build());
            await subscriber.SubscribeAsync("conwatch/#");
            await subscriber.SubscribeAsync("renamed/#");

            using var factory = new ConWatchFactory();
            var client = factory.CreateClient();
            await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
            {
                ["Mqtt.Enabled"] = "true",
                ["Mqtt.Host"] = "127.0.0.1",
                ["Mqtt.Port"] = brokerPort.ToString(),
            });
            await ConfigApiTests.SeedHomeAsync(client);

            // Traffic first: every processed item used to orphan one wake waiter.
            await ConfigApiTests.Eventually(() =>
            {
                lock (messages)
                    return Task.FromResult(
                        messages.GetValueOrDefault("conwatch/availability") == "online" &&
                        messages.Any(m => m.Key.StartsWith("conwatch/target/") && m.Value == "ON"));
            });

            // ONE settings change must end the session and reconnect under the new base topic.
            await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
            {
                ["Mqtt.BaseTopic"] = "renamed",
            });
            await ConfigApiTests.Eventually(() =>
            {
                lock (messages)
                    return Task.FromResult(
                        messages.GetValueOrDefault("renamed/availability") == "online" &&
                        messages.GetValueOrDefault("conwatch/availability") == "offline");
            }, timeoutMs: 15000);

            // The first transition after the reconnect must be published, not eaten
            // by an orphaned reader from the previous session.
            factory.Pings.SetUp(IPAddress.Parse("192.168.1.1"), false);
            await ConfigApiTests.Eventually(() =>
            {
                lock (messages)
                    return Task.FromResult(messages.Keys.Any(k => k.StartsWith("renamed/target/")) &&
                        messages.Any(m => m.Key.StartsWith("renamed/target/") && m.Value == "OFF"));
            }, timeoutMs: 15000);
        }
        finally
        {
            RunDocker($"rm -f {containerId}");
        }
    }

    // ---------------- Task 24: wire-level MQTT and webhook behaviour ----------------

    private const string VerdictAttrs = "{\"critical\":false,\"scope\":\"Verdict\"}";
    private const string LocalAttrs = "{\"critical\":false,\"scope\":\"Local\"}";

    /// <summary>A subscriber that is connected BEFORE the app publishes, so it sees live (non-retained) traffic.</summary>
    private sealed class LiveSubscriber : IDisposable
    {
        private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
        private readonly Dictionary<string, string> _last = new();
        private readonly List<(string Topic, string Payload)> _all = new();

        public static async Task<LiveSubscriber> StartAsync(int port, params string[] filters)
        {
            var sub = new LiveSubscriber();
            sub._client.ApplicationMessageReceivedAsync += e =>
            {
                var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";
                lock (sub._last)
                {
                    sub._last[e.ApplicationMessage.Topic] = payload;
                    sub._all.Add((e.ApplicationMessage.Topic, payload));
                }
                return Task.CompletedTask;
            };
            await sub._client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
            foreach (var filter in filters)
                await sub._client.SubscribeAsync(filter);
            return sub;
        }

        /// <summary>The latest payload on the topic, or null when nothing arrived.</summary>
        public string? Last(string topic)
        {
            lock (_last) return _last.GetValueOrDefault(topic);
        }

        public List<string> Payloads(string topic)
        {
            lock (_last) return _all.Where(m => m.Topic == topic).Select(m => m.Payload).ToList();
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>
    /// A client that connects after the app has published sees only what the broker retained
    /// (MQTT 3.1.1 replays nothing else), so its collection IS the retained set. It collects for a fixed
    /// window once the retained availability message has arrived (the absence window follows a positive condition).
    /// </summary>
    private static async Task<Dictionary<string, (string Payload, bool Retain)>> RetainedSetAsync(int port)
    {
        var set = new Dictionary<string, (string Payload, bool Retain)>();
        using var late = new MqttClientFactory().CreateMqttClient();
        late.ApplicationMessageReceivedAsync += e =>
        {
            lock (set)
                set[e.ApplicationMessage.Topic] =
                    (e.ApplicationMessage.ConvertPayloadToString() ?? "", e.ApplicationMessage.Retain);
            return Task.CompletedTask;
        };
        await late.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build());
        await late.SubscribeAsync("conwatch/#");
        await late.SubscribeAsync("homeassistant/#");
        await HostTestWait.Eventually(() =>
        {
            lock (set) return Task.FromResult(set.ContainsKey("conwatch/availability"));
        }, 15000);
        await Task.Delay(3000);
        lock (set) return new Dictionary<string, (string, bool)>(set);
    }

    private static Task EnableMqttAsync(HttpClient client, int brokerPort) =>
        client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Mqtt.Enabled"] = "true",
            ["Mqtt.Host"] = "127.0.0.1",
            ["Mqtt.Port"] = brokerPort.ToString(),
        });

    /// <summary>Seeds the home config, waits for the rebuilds to settle with the engine running, and only
    /// then enables MQTT, so the session starts against a quiet engine. This ordering once also dodged the
    /// wipe-on-rebuild bug (audit defect 1); that is fixed, and
    /// A_rebuild_that_adds_no_target_keeps_every_target_entity_retained pins it.</summary>
    private static async Task SeedThenEnableMqttAsync(ConWatchFactory factory, HttpClient client, int brokerPort)
    {
        await ConfigApiTests.SeedHomeAsync(client);
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await HostTestWait.Eventually(() => Task.FromResult(host.EngineRunning));
        await HostTestWait.SettledRebuildCountAsync(host);
        await EnableMqttAsync(client, brokerPort);
    }

    private static async Task PutSettingAsync(HttpClient client, string key, string value) =>
        (await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?> { [key] = value }))
            .EnsureSuccessStatusCode();

    private static HashSet<string> VerdictStateNames()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ContractFiles.PathOf("vocabulary.json")));
        return doc.RootElement.GetProperty("VerdictState").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
    }

    [SkippableFact]
    public async Task Retained_state_is_what_a_late_subscriber_sees()
    {
        var (brokerPort, containerId) = RequireMosquitto();
        try
        {
            await WaitForBrokerAsync(brokerPort);
            using var live = await LiveSubscriber.StartAsync(brokerPort, "conwatch/#", "homeassistant/#");
            using var factory = new ConWatchFactory();
            var client = factory.CreateClient();
            await SeedThenEnableMqttAsync(factory, client, brokerPort);

            // The config settled before MQTT came on, so nothing transitions on its own any more: blip one
            // anchor to put a transition event on the live stream, then let it recover.
            var google = IPAddress.Parse("8.8.8.8");
            await HostTestWait.Eventually(() => Task.FromResult(live.Last("conwatch/target/google/state") == "ON"));
            factory.Pings.SetUp(google, false);
            await HostTestWait.Eventually(() => Task.FromResult(live.Last("conwatch/target/google/state") == "OFF"));
            factory.Pings.SetUp(google, true);

            await HostTestWait.Eventually(() => Task.FromResult(
                live.Last("conwatch/availability") == "online"
                && live.Last("conwatch/verdict/state") == "Healthy"
                && live.Last("conwatch/internet/state") == "ON"
                && new[] { "router", "google", "cloudflare" }.All(s => live.Last($"conwatch/target/{s}/state") == "ON")
                && live.Payloads("conwatch/transitions").Count > 0));

            // The live stream carries transitions as events.
            var transition = JsonSerializer.Deserialize<JsonElement>(live.Payloads("conwatch/transitions")[0]);
            Assert.True(transition.TryGetProperty("subjectKind", out _));
            Assert.True(transition.TryGetProperty("to", out _));

            var retained = await RetainedSetAsync(brokerPort);

            Assert.Equal("online", retained["conwatch/availability"].Payload);
            Assert.Contains(retained["conwatch/verdict/state"].Payload, VerdictStateNames());
            Assert.Equal("ON", retained["conwatch/internet/state"].Payload);
            foreach (var slug in new[] { "router", "google", "cloudflare" })
            {
                Assert.Equal("ON", retained[$"conwatch/target/{slug}/state"].Payload);
                Assert.Equal(VerdictAttrs, retained[$"conwatch/target/{slug}/attributes"].Payload);
            }
            Assert.Contains("homeassistant/sensor/conwatch_verdict/config", retained.Keys);
            Assert.Contains("homeassistant/binary_sensor/conwatch_internet/config", retained.Keys);
            Assert.Contains("homeassistant/binary_sensor/conwatch_dns/config", retained.Keys);
            // No system resolver is configured here, so the name-resolution entity is unwatched: its own
            // availability says so, and its state stays ON rather than alarming an automation.
            Assert.Equal("offline", retained["conwatch/dns/availability"].Payload);
            Assert.Equal("ON", retained["conwatch/dns/state"].Payload);
            // Events are not state: a late subscriber must not be replayed an old transition.
            Assert.DoesNotContain("conwatch/transitions", retained.Keys);
            // Everything a late subscriber got was replayed from the retained store.
            Assert.All(retained, m => Assert.True(m.Value.Retain, $"{m.Key} was not retained"));

            // A deleted target takes its entity with it.
            (await client.PostAsJsonAsync("/api/v1/targets", new
            {
                name = "Printer", address = "203.0.113.30", parent = "Router", profile = "Fast",
            })).EnsureSuccessStatusCode();
            const string printerConfig = "homeassistant/binary_sensor/conwatch_printer/config";
            await HostTestWait.Eventually(() => Task.FromResult(!string.IsNullOrEmpty(live.Last(printerConfig))));
            Assert.Contains(printerConfig, (await RetainedSetAsync(brokerPort)).Keys);

            var targets = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/targets");
            var printerId = targets!.Single(t => t.GetProperty("name").GetString() == "Printer").GetProperty("id").GetGuid();
            (await client.DeleteAsync($"/api/v1/targets/{printerId}")).EnsureSuccessStatusCode();
            await HostTestWait.Eventually(() => Task.FromResult(live.Last(printerConfig) == ""));

            var afterDelete = await RetainedSetAsync(brokerPort);
            Assert.DoesNotContain(printerConfig, afterDelete.Keys);
            Assert.DoesNotContain("conwatch/target/printer/state", afterDelete.Keys);
            // The survivors keep theirs: the rebuild's refresh waits for the engine to start (it used to read
            // the monitors before StartAsync had created them and clear every target; audit defect 1, fixed).
            foreach (var slug in new[] { "router", "google", "cloudflare" })
            {
                Assert.Contains($"homeassistant/binary_sensor/conwatch_{slug}/config", afterDelete.Keys);
                Assert.Equal("ON", afterDelete[$"conwatch/target/{slug}/state"].Payload);
                Assert.Equal(VerdictAttrs, afterDelete[$"conwatch/target/{slug}/attributes"].Payload);
            }
        }
        finally
        {
            RunDocker($"rm -f {containerId}");
        }
    }

    [SkippableFact]
    public async Task A_rebuild_that_adds_no_target_keeps_every_target_entity_retained()
    {
        var (brokerPort, containerId) = RequireMosquitto();
        try
        {
            await WaitForBrokerAsync(brokerPort);
            using var live = await LiveSubscriber.StartAsync(brokerPort, "conwatch/#", "homeassistant/#");
            using var factory = new ConWatchFactory();
            var client = factory.CreateClient();
            await SeedThenEnableMqttAsync(factory, client, brokerPort);
            var slugs = new[] { "router", "google", "cloudflare" };
            await HostTestWait.Eventually(() => Task.FromResult(
                slugs.All(s => live.Last($"conwatch/target/{s}/state") == "ON")));

            var host = factory.Services.GetRequiredService<WatcherHostService>();
            var rebuilds = host.RebuildCount;
            // Every refresh republishes the verdict's discovery, so its count marks the refreshes.
            const string verdictConfig = "homeassistant/sensor/conwatch_verdict/config";
            var refreshes = live.Payloads(verdictConfig).Count;

            // A warm rebuild that adds no target and makes none transition: flag the Router critical.
            var router = (await client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets"))!
                .Single(t => t.Name == "Router");
            (await client.PutAsJsonAsync($"/api/v1/targets/{router.Id}", router with { Critical = true }))
                .EnsureSuccessStatusCode();
            await HostTestWait.Eventually(() => Task.FromResult(host.RebuildCount > rebuilds && host.EngineRunning));
            await HostTestWait.SettledRebuildCountAsync(host);
            await HostTestWait.Eventually(() => Task.FromResult(live.Payloads(verdictConfig).Count > refreshes));

            var retained = await RetainedSetAsync(brokerPort);
            foreach (var slug in slugs)
            {
                Assert.Contains($"homeassistant/binary_sensor/conwatch_{slug}/config", retained.Keys);
                Assert.Contains($"conwatch/target/{slug}/state", retained.Keys);
                Assert.Equal("ON", retained[$"conwatch/target/{slug}/state"].Payload);
            }
            // The rebuild's own change reached Home Assistant too.
            Assert.Equal("{\"critical\":true,\"scope\":\"Verdict\"}", retained["conwatch/target/router/attributes"].Payload);
            Assert.Equal(VerdictAttrs, retained["conwatch/target/google/attributes"].Payload);
            Assert.Equal(VerdictAttrs, retained["conwatch/target/cloudflare/attributes"].Payload);
        }
        finally
        {
            RunDocker($"rm -f {containerId}");
        }
    }

    [SkippableFact]
    public async Task Local_devices_get_sensors_too()
    {
        var (brokerPort, containerId) = RequireMosquitto();
        try
        {
            await WaitForBrokerAsync(brokerPort);
            using var live = await LiveSubscriber.StartAsync(brokerPort, "conwatch/#");
            using var factory = new ConWatchFactory();
            var client = factory.CreateClient();
            await SeedThenEnableMqttAsync(factory, client, brokerPort);

            var ip = IPAddress.Parse("203.0.113.9");
            (await client.PostAsJsonAsync("/api/v1/targets", new
            {
                name = "Nas", address = "203.0.113.9", profile = "Fast", scope = "Local",
            })).EnsureSuccessStatusCode();
            await HostTestWait.Eventually(() => Task.FromResult(
                live.Last("conwatch/target/nas/state") == "ON"
                && live.Last("conwatch/target/nas/attributes") == LocalAttrs));

            // A LAN device's own transitions reach MQTT as well (webhooks leave them out; MQTT never does).
            factory.Pings.SetUp(ip, false);
            await HostTestWait.Eventually(() => Task.FromResult(live.Last("conwatch/target/nas/state") == "OFF"));

            var retained = await RetainedSetAsync(brokerPort);
            Assert.Equal("OFF", retained["conwatch/target/nas/state"].Payload);
            Assert.Equal(LocalAttrs, retained["conwatch/target/nas/attributes"].Payload);
        }
        finally
        {
            RunDocker($"rm -f {containerId}");
        }
    }

    [SkippableFact]
    public async Task Master_enable_takes_mqtt_offline_and_back()
    {
        var (brokerPort, containerId) = RequireMosquitto();
        try
        {
            await WaitForBrokerAsync(brokerPort);
            using var live = await LiveSubscriber.StartAsync(brokerPort, "conwatch/#");
            using var factory = new ConWatchFactory();
            var client = factory.CreateClient();
            await SeedThenEnableMqttAsync(factory, client, brokerPort);
            await HostTestWait.Eventually(() => Task.FromResult(
                live.Last("conwatch/availability") == "online" && live.Last("conwatch/verdict/state") == "Healthy"));

            await PutSettingAsync(client, "Engine.Enabled", "false");
            await HostTestWait.Eventually(() => Task.FromResult(live.Last("conwatch/availability") == "offline"));
            // It must STAY offline: a notifier that ignored the switch would reconnect and say online again.
            await Task.Delay(2000);
            Assert.Equal("offline", live.Last("conwatch/availability"));

            await PutSettingAsync(client, "Engine.Enabled", "true");
            await HostTestWait.Eventually(() => Task.FromResult(live.Last("conwatch/availability") == "online"));
        }
        finally
        {
            RunDocker($"rm -f {containerId}");
        }
    }

    [Fact]
    public async Task Heartbeat_stops_while_the_engine_is_disabled_and_resumes()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();

        await PutSettingAsync(client, "Heartbeat.Url", hook.Url("/check-a"));
        await HostTestWait.Eventually(() => Task.FromResult(hook.Received.Any(r => r.Path == "/check-a")));

        await PutSettingAsync(client, "Engine.Enabled", "false");
        await PutSettingAsync(client, "Heartbeat.Url", hook.Url("/check-c"));
        await Task.Delay(3000); // absence window, after the first ping proved the heartbeat was live
        Assert.DoesNotContain(hook.Received, r => r.Path == "/check-c");

        await PutSettingAsync(client, "Engine.Enabled", "true");
        await HostTestWait.Eventually(() => Task.FromResult(hook.Received.Any(r => r.Path == "/check-c")));
    }

    private static readonly string[] OutageAddresses = ["192.168.1.1", "8.8.8.8", "1.1.1.1"];

    /// <summary>
    /// Takes the whole path down (Router, then Google and Cloudflare behind it) and returns what the webhook
    /// received afterwards. With <paramref name="expectUnreachable"/> it waits for the Unreachable deliveries;
    /// otherwise it waits until the engine has those states, then allows a window for a stray to arrive.
    /// </summary>
    private static async Task<List<JsonElement>> OutageDeliveriesAsync(string? skipExplained, bool expectUnreachable)
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        var settings = new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.Urls"] = hook.Url("/hook"),
        };
        if (skipExplained is not null)
            settings["Webhook.SkipExplained"] = skipExplained;
        (await client.PutAsJsonAsync("/api/v1/settings", settings)).EnsureSuccessStatusCode();
        await ConfigApiTests.SeedHomeAsync(client);
        await HostTestWait.Eventually(() => HostTestWait.SteadyStateAsync(client));
        // The startup deliveries are FIFO behind the engine; wait until the queue has drained.
        var count = -1;
        var stableSince = DateTime.UtcNow;
        await HostTestWait.Eventually(() =>
        {
            if (hook.Received.Count != count)
            {
                count = hook.Received.Count;
                stableSince = DateTime.UtcNow;
            }
            return Task.FromResult(DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(750));
        });
        var baseline = count;

        List<JsonElement> Deliveries() => hook.Received.Skip(baseline)
            .Select(r => JsonSerializer.Deserialize<JsonElement>(r.Body)).ToList();
        static bool Is(JsonElement d, string kind, string name, string to) =>
            d.GetProperty("subjectKind").GetString() == kind
            && d.GetProperty("subjectName").GetString() == name
            && d.GetProperty("to").GetString() == to;

        // The Router fails first, so the anchors failing behind it are explained by it.
        factory.Pings.SetUp(IPAddress.Parse(OutageAddresses[0]), false);
        await HostTestWait.Eventually(() => Task.FromResult(Deliveries().Any(d => Is(d, "Target", "Router", "Down"))));
        foreach (var address in OutageAddresses.Skip(1))
            factory.Pings.SetUp(IPAddress.Parse(address), false);
        await HostTestWait.Eventually(() => Task.FromResult(
            Deliveries().Any(d => d.GetProperty("subjectKind").GetString() == "Verdict"
                                  && d.GetProperty("verdict").GetString() == "LinkDown")));
        if (expectUnreachable)
        {
            await HostTestWait.Eventually(() => Task.FromResult(
                Deliveries().Any(d => Is(d, "Target", "Google", "Unreachable"))
                && Deliveries().Any(d => Is(d, "Target", "Cloudflare", "Unreachable"))));
        }
        else
        {
            // The engine has explained both anchors; their deliveries (if any were wrongly queued) are on the way.
            await HostTestWait.Eventually(async () =>
            {
                var status = JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync("/api/v1/status"));
                return status.GetProperty("targets").EnumerateArray().Count(t =>
                    t.GetProperty("state").GetString() == "Unreachable") >= 2;
            });
            await Task.Delay(1500);
        }
        return Deliveries();
    }

    [Fact]
    public async Task Explained_failures_are_skipped_by_default()
    {
        // Webhook.SkipExplained is deliberately never written here: skipping is the default.
        var deliveries = await OutageDeliveriesAsync(skipExplained: null, expectUnreachable: false);

        var routerDown = deliveries.FindIndex(d =>
            d.GetProperty("subjectName").GetString() == "Router" && d.GetProperty("to").GetString() == "Down");
        var linkDown = deliveries.FindIndex(d =>
            d.GetProperty("subjectKind").GetString() == "Verdict" && d.GetProperty("verdict").GetString() == "LinkDown");
        Assert.True(routerDown >= 0, "Router Down was not delivered");
        Assert.True(linkDown > routerDown, "the LinkDown verdict should follow Router Down");
        Assert.DoesNotContain(deliveries, d => d.GetProperty("to").GetString() == "Unreachable");
    }

    [Fact]
    public async Task Explained_failures_are_delivered_when_skipping_is_switched_off()
    {
        var deliveries = await OutageDeliveriesAsync(skipExplained: "false", expectUnreachable: true);

        Assert.Contains(deliveries, d => d.GetProperty("subjectName").GetString() == "Google"
                                         && d.GetProperty("to").GetString() == "Unreachable");
        Assert.Contains(deliveries, d => d.GetProperty("subjectName").GetString() == "Cloudflare"
                                         && d.GetProperty("to").GetString() == "Unreachable");
    }

    [Fact]
    public async Task Each_webhook_url_on_its_own_line_is_called()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        // Indented and blank lines between the entries must not matter.
        await PutSettingAsync(client, "Webhook.Enabled", "true");
        await PutSettingAsync(client, "Webhook.Urls", hook.Url("/first") + "\n " + "\n\n" + hook.Url("/second") + "\n");

        await ConfigApiTests.SeedHomeAsync(client);
        await HostTestWait.Eventually(() => Task.FromResult(
            hook.Received.Any(r => r.Path == "/first") && hook.Received.Any(r => r.Path == "/second")));
    }

    private static string RunDocker(string args)
    {
        var psi = new ProcessStartInfo("docker", args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);
        return proc.ExitCode == 0 ? output : "";
    }

    // A plain TCP connect proves nothing here: Docker's port proxy accepts on the
    // host port before Mosquitto listens inside the container, then drops the
    // connection ("Error while authenticating. Connection closed." in CI). Only
    // a real CONNACK means the broker is up.
    private static async Task WaitForBrokerAsync(int port)
    {
        var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            using var probe = new MqttClientFactory().CreateMqttClient();
            try
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await probe.ConnectAsync(options, attempt.Token);
                await probe.DisconnectAsync();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
            }
        }
    }
}
