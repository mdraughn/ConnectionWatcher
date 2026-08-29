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

    [Fact]
    public async Task Transitions_are_delivered_to_a_webhook()
    {
        var port = FreePort();
        var received = new List<string>();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var pump = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    lock (received) received.Add(body);
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                }
                catch (Exception) when (!listener.IsListening) { return; }
            }
        });

        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        var put = await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.Urls"] = $"http://127.0.0.1:{port}/hook",
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        await ConfigApiTests.SeedHomeAsync(client);
        await ConfigApiTests.Eventually(() =>
        {
            lock (received) return Task.FromResult(received.Count > 0);
        });

        string first;
        lock (received) first = received[0];
        var payload = JsonSerializer.Deserialize<JsonElement>(first);
        Assert.True(payload.TryGetProperty("to", out _));
        Assert.True(payload.TryGetProperty("subjectKind", out _));
        listener.Stop();
        await pump;
    }

    [Fact]
    public async Task VerdictOnly_suppresses_target_transitions()
    {
        var port = FreePort();
        int hits = 0;
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var pump = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var ctx = await listener.GetContextAsync();
                    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    var kind = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("subjectKind").GetString();
                    Assert.Equal("Verdict", kind);
                    Interlocked.Increment(ref hits);
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                }
                catch (Exception) when (!listener.IsListening) { return; }
            }
        });

        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.VerdictOnly"] = "true",
            ["Webhook.Urls"] = $"http://127.0.0.1:{port}/hook",
        });

        // SeedHome creates an anchor pool, so the verdict transitions to Healthy.
        await ConfigApiTests.SeedHomeAsync(client);
        await ConfigApiTests.Eventually(() => Task.FromResult(Volatile.Read(ref hits) > 0));
        listener.Stop();
        await pump;
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

    [SkippableFact]
    public async Task Mqtt_publishes_availability_discovery_and_states_to_a_real_broker()
    {
        Skip.IfNot(DockerAvailable(), "Docker engine is not running");

        var brokerPort = FreePort();
        string containerId = RunDocker(
            $"run -d --rm -p {brokerPort}:1883 eclipse-mosquitto:2 mosquitto -c /mosquitto-no-auth.conf").Trim();
        Skip.If(containerId.Length == 0, "Mosquitto container failed to start");
        try
        {
            await WaitForPortAsync(brokerPort);

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

    private static string RunDocker(string args)
    {
        var psi = new ProcessStartInfo("docker", args)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(30000);
        return proc.ExitCode == 0 ? output : "";
    }

    private static async Task WaitForPortAsync(int port)
    {
        for (int i = 0; i < 100; i++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(200);
            }
        }
        throw new TimeoutException($"Mosquitto never listened on {port}");
    }
}
