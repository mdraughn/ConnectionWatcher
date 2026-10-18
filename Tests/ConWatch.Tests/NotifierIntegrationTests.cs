using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) when (!_listener.IsListening) { return; }
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
                    if (!_listener.IsListening)
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
