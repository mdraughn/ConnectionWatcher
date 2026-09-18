# Review Fixes (2026-09-18) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the correctness and durability findings of the 2026-09-18 architecture review (H1, E1–E6, P1–P7, H2–H5, S1, S5, S10) without changing the architecture; the pure structure and dedupe findings (S2–S4, S6–S9, S11, report section 3) get a second plan after this one lands.

**Architecture:** The engine keeps its shape (PingManager → channel → single result thread → TargetMonitor → coordinators → Rx subjects → channel-buffered consumers). Changes are local: transceivers rethrow cancellation instead of reporting it as loss; the result pump guards per message and stamps its last result so `/health` can detect a stall; the MQTT notifier's wake semaphore becomes a message on its existing channel; recorders retry a pending batch on a timer and coalesce duplicate rollup buckets; host observers share one `IEngineObserver` interface with `Detach()`; the DbContext restores `DateTimeKind.Utc` on read.

**Tech Stack:** .NET 10, xUnit, FakeTimeProvider (Microsoft.Extensions.Time.Testing), System.Reactive, EF Core SQLite, ASP.NET Core minimal APIs + Razor Pages, MQTTnet 5, React + vitest for the History page.

**Spec:** `docs/superpowers/reviews/2026-09-18-codebase-review.md` (findings by id; section 5 has the sequencing and the owner's seven decisions, all made 2026-09-18).

## Decisions recorded from the review (2026-09-18)

- **E4**: sweep cadence is 60 s. Delete `SweepCooldown`; the discipline's `SuspicionGuard` is the only rule.
- **P3**: seed pre-window outages in BOTH the report and the History page. An outage open at the window start counts, clipped to the start.
- **S3**: union of ancestor chains everywhere (not in this plan; plan B).
- **P5**: delete `PRAGMA incremental_vacuum`; space reclamation stays on the Data page's VACUUM.
- **H5**: `stop_grace_period: 30s` in compose + Synology note; `HostOptions.ShutdownTimeout` = 25 s; engine grace 2 + 2 s; MQTT goodbye bounded by a 2 s token.
- **DomainResolutionResult**: keep its shape; the engine and the Test button surface `Success`/`Status`.
- **Reviews** live under `docs/superpowers/reviews/`.

Re-checked while writing this plan and **dropped**: the review's H6 "winning debounce CTS never disposed" claim. `WatcherHostService.OnConfigChanged:326-327` cancels and disposes the previous CTS on every change, winner or not, so at most one CTS is outstanding, not one per rebuild. Only `StopAsync` leaves one behind; Task 4.2 disposes it there. The review file gets a one-line correction in Task 4.2.

## Global Constraints

- No behavior change without a test that fails first, except pure moves and renames and the refactors explicitly marked "under existing tests".
- Existing public behavior of the REST API stays byte-compatible except where a task says otherwise (Task 3.4: JSON export and snapshot timestamps gain a `Z` suffix; `/admin/engine` answers 500 on a failed rebuild in Task 4.2; `/health` gains `stalled` and the `EngineStalled` status in Task 2.5).
- Never push to origin. Commit per task on a branch: `git switch -c review-2026-09-18` from master before Task 1.1.
- Baseline before Task 1.1: `dotnet test ConnectionWatcher.sln` passes 434/434 (157 engine, 169 web, 79 persistence, 29 notify), zero warnings.
- Test commands run from the repo root. Client tests: `cd Programs/ConWatch/ClientApp && npm test`.
- All timestamps stay `DateTimeOffset`/UTC end to end (existing rule).
- `Directory.Build.props` enables warnings-as-errors for nothing today; keep the build at zero warnings anyway (the 2026-09-17 tidy got it there).

## Ordering and rationale

| Phase | Tasks | Why here |
|---|---|---|
| 1 Notifier | 1.1 | H1 is the only HIGH and is self-contained. |
| 2 Engine | 2.1–2.7 | E2 (cancellation) first: it removes the spurious samples that would otherwise confuse E1's stall stamp. 2.3 (clock-stamped results) is the prerequisite for 2.4 (last-result stamp) and 2.5 (`/health` stall). 2.6/2.7 are small and independent. |
| 3 Persistence | 3.1–3.5 | Independent of the engine. 3.4 (UTC converter) before 3.5 so the `SpecifyKind` cleanup is one pass. |
| 4 Host | 4.1–4.3 | 4.1 (`IEngineObserver` + `Detach`) touches seven observers, so it goes after the notifier and recorder tasks that edit the same files. 4.2/4.3 are small. |

---

## Phase 1: Notifier

### Task 1.1: MqttNotifier reconfigures through its channel; the wake semaphore goes away (H1, S5)

**Files:**
- Modify: `Libraries/ConNotify/MqttNotifier.cs:19-24, 32-45, 47-78, 107-124`
- Test: `Tests/ConWatch.Tests/NotifierIntegrationTests.cs` (extend the Docker-backed MQTT test)
- Test: `Tests/ConNotify.Tests/MqttNotifierInboxTests.cs` (new)

**Interfaces:**
- Consumes: `SettingsStore.NotifierSettingsChanged` (event, `Libraries/ConPersistence/SettingsStore.cs:121-127`).
- Produces: `MqttNotifier.Reconfigure` (private record), `MqttNotifier.WaitForReconfigureAsync(CancellationToken)` (private). Nothing outside the class changes; Task 4.1 later adds `Detach()`.

**Why the shape below:** `MqttNotifier.cs:110-118` creates a fresh `_queue.Reader.ReadAsync` and a fresh `_wake.WaitAsync` on every loop turn and abandons the loser. Abandoned semaphore waiters stay queued and eat the next `Release()`; abandoned channel readers stay registered and eat the next transition. With every input travelling through `_queue` there is exactly one reader and nothing to orphan.

- [ ] **Step 1: Write the failing integration test (Docker-backed; skips without Docker)**

Add to `Tests/ConWatch.Tests/NotifierIntegrationTests.cs`, after `Mqtt_publishes_availability_discovery_and_states_to_a_real_broker`:

```csharp
    [SkippableFact]
    public async Task Mqtt_settings_change_after_traffic_reconnects_on_the_new_base_topic()
    {
        Skip.IfNot(DockerAvailable(), "Docker engine is not running");

        var brokerPort = FreePort();
        string containerId = RunDocker(
            $"run -d --rm -p {brokerPort}:1883 eclipse-mosquitto:2 mosquitto -c /mosquitto-no-auth.conf").Trim();
        Skip.If(containerId.Length == 0, "Mosquitto container failed to start");
        try
        {
            await WaitForPortAsync(brokerPort);

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
```

- [ ] **Step 2: Write the failing unit test for the inbox rule**

Create `Tests/ConNotify.Tests/MqttNotifierInboxTests.cs`. This tests the private inbox through a small public seam added in Step 4 (`MqttNotifier.Inbox`), so the rule "one poke, one reconfigure, nothing orphaned" is checked without a broker.

```csharp
using ConEngine.Events;
using ConEngine.Tracking;
using ConNotify;

namespace ConNotify.Tests;

public class MqttNotifierInboxTests
{
    private static TargetTransition Transition(string name) => new()
    {
        TargetId = Guid.NewGuid(), TargetName = name,
        From = HostState.Responding, To = HostState.Down,
        Timestamp = DateTimeOffset.UtcNow,
        Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed },
    };

    [Fact]
    public async Task One_reconfigure_after_traffic_is_read_after_that_traffic()
    {
        var inbox = new MqttNotifier.Inbox();
        inbox.Post(Transition("A"));
        inbox.Post(Transition("B"));
        inbox.Post(Transition("C"));
        inbox.Reconfigure();

        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<MqttNotifier.Inbox.ReconfigureMessage>(await inbox.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Waiting_for_a_reconfigure_skips_traffic_and_returns_on_the_poke()
    {
        var inbox = new MqttNotifier.Inbox();
        inbox.Post(Transition("A"));
        inbox.Refresh();
        var waiting = inbox.WaitForReconfigureAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        inbox.Reconfigure();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        // Nothing is left behind for the next session to trip over.
        inbox.Post(Transition("D"));
        var next = await inbox.ReadAsync(CancellationToken.None);
        Assert.Equal("D", Assert.IsType<TargetTransition>(next).TargetName);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test Tests/ConNotify.Tests --filter "FullyQualifiedName~MqttNotifierInboxTests"`
Expected: build FAILS with "'MqttNotifier' does not contain a definition for 'Inbox'".

Run (only if Docker is up): `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Mqtt_settings_change_after_traffic"`
Expected: FAIL at the second `Eventually` ("Condition not met within timeout"): the base-topic change is swallowed by an orphaned waiter.

- [ ] **Step 4: Implement**

Replace `Libraries/ConNotify/MqttNotifier.cs:19-24` (the `Refresh` record, `_queue`, `_wake`, `_lock` fields) with:

```csharp
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
```

Replace `Attach` (`:32-45`) with:

```csharp
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
```

In `ExecuteAsync` (`:47-78`), change line 49 and the disabled wait at line 62:

```csharp
        settings.NotifierSettingsChanged += _inbox.Reconfigure;
        ...
                if (!enabled || !engineEnabled || host is not { Length: > 0 })
                {
                    await _inbox.WaitForReconfigureAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }
```

Replace the session loop (`:107-124`) with:

```csharp
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
```

In `PublishTransitionAsync` (`:206`) replace `_queue.Writer.TryWrite(new Refresh());` with `_inbox.Refresh();`. Delete every remaining reference to `_wake` and the old `Refresh` record (the compiler will list them).

- [ ] **Step 5: Run the notify suite and the Docker test**

Run: `dotnet test Tests/ConNotify.Tests`
Expected: all pass, including the two new inbox tests.

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~NotifierIntegrationTests"`
Expected: all pass (the two MQTT tests skip without Docker; with Docker both pass).

- [ ] **Step 6: Commit**

```bash
git add Libraries/ConNotify/MqttNotifier.cs Tests/ConNotify.Tests/MqttNotifierInboxTests.cs Tests/ConWatch.Tests/NotifierIntegrationTests.cs
git commit -m "fix(notify): MQTT reconfigure travels through the inbox channel; no orphaned wake waiters or readers"
```

---
## Phase 2: Engine

### Task 2.1: Cancellation throws instead of reporting a lost ping (E2)

**Files:**
- Modify: `Libraries/ConInfrastructure/PingTransceiverIcmp.cs:58-69`
- Modify: `Libraries/ConInfrastructure/TcpConnectTransceiver.cs:36-55`
- Modify: `Libraries/ConInfrastructure/DomainNameResolverDns.cs:69-80, 126-137`
- Modify: `Core/ConEngine/Network/PingController.cs:80-82`
- Modify: `Core/ConCore/IPingTransceiver.cs:15-21`, `Core/ConCore/IDomainNameResolver.cs:8-14` (doc comments)
- Test: `Tests/ConEngine.Tests/PingTransceiverIcmpTests.cs:9-22`
- Test: `Tests/ConEngine.Tests/TcpConnectTransceiverTests.cs` (add one test)
- Test: `Tests/ConEngine.Tests/DomainNameResolverDnsTests.cs` (new)
- Test: `Tests/ConEngine.Tests/PingControllerTests.cs` (add one test)

**Interfaces:**
- Consumes: `IPingTransceiver.PingAsync(int timeoutMs)`, `IDomainNameResolver.ResolveAsync(string, int)` (signatures unchanged).
- Produces: a documented contract on both interfaces: an `OperationCanceledException` from the token given at construction propagates; a probe *timeout* is still a failed `PingResult` ("TimedOut").

- [ ] **Step 1: Write the failing tests**

Replace `Cancellation_ends_an_in_flight_ping_before_its_timeout` in `Tests/ConEngine.Tests/PingTransceiverIcmpTests.cs` with:

```csharp
    [Fact]
    public async Task Cancellation_ends_an_in_flight_ping_before_its_timeout_and_throws()
    {
        // 192.0.2.1 is TEST-NET-1: never routed, never answers.
        using var cts = new CancellationTokenSource();
        using var ping = new PingTransceiverIcmp(IPAddress.Parse("192.0.2.1"), cts.Token);
        var sw = Stopwatch.StartNew();
        var task = ping.PingAsync(timeoutMs: 10_000);
        cts.CancelAfter(200);

        // The engine's own cancellation is not a lost packet: it must propagate,
        // not come back as a failed sample that the tracker would record.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
```

Add to `Tests/ConEngine.Tests/TcpConnectTransceiverTests.cs`:

```csharp
    [Fact]
    public async Task Engine_cancellation_throws_instead_of_reporting_loss()
    {
        using var cts = new CancellationTokenSource();
        using var probe = new TcpConnectTransceiver(IPAddress.Parse("192.0.2.1"), 80, cts.Token);
        var task = probe.PingAsync(5000);
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
```

Create `Tests/ConEngine.Tests/DomainNameResolverDnsTests.cs`:

```csharp
using ConInfrastructure;

namespace ConEngine.Tests;

public class DomainNameResolverDnsTests
{
    [Fact]
    public async Task Cancelled_token_throws_instead_of_reporting_a_failed_lookup()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var resolver = new DomainNameResolverDns(cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync("example.invalid", 5000));
    }

    [Fact]
    public async Task Unknown_host_is_a_failed_result_with_a_reason()
    {
        using var resolver = new DomainNameResolverDns(CancellationToken.None);
        var result = await resolver.ResolveAsync("nonexistent.invalid", 5000);
        Assert.False(result.Success);
        Assert.NotEqual("Success", result.Status);
        Assert.Empty(result.IpAddresses);
    }
}
```

Add to `Tests/ConEngine.Tests/PingControllerTests.cs` (inside the class):

```csharp
    /// <summary>The OLD production transceiver behavior: cancellation comes back
    /// as a failed sample. The controller must still not forward it.</summary>
    private sealed class CancelReportingFactory : IPingTransceiverFactory
    {
        public IPingTransceiver Create(IPAddress ip, ProbeSpec probe, CancellationToken ct) => new Probe(ip, ct);

        private sealed class Probe(IPAddress ip, CancellationToken ct) : IPingTransceiver
        {
            public IPAddress IpAddress => ip;

            public async Task<PingResult> PingAsync(int timeoutMs)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { }
                return new PingResult(ip, false, 0, "Cancelled");
            }

            public void Dispose() { }
        }
    }

    [Fact]
    public async Task A_probe_cancelled_mid_flight_writes_nothing_to_the_channel()
    {
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(new CancelReportingFactory(), NullLogger.Instance);

        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(1), TimeProvider.System),
            () => 60_000, cts.Token);
        await Task.Delay(100); // the loop is inside its first (hanging) probe

        await controller.StopAsync(TimeSpan.FromSeconds(2));

        Assert.False(channel.Reader.TryRead(out _), "a cancelled probe must not become a lost sample");
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingTransceiverIcmpTests|FullyQualifiedName~TcpConnectTransceiverTests|FullyQualifiedName~DomainNameResolverDnsTests|FullyQualifiedName~A_probe_cancelled"`
Expected: `Cancellation_ends_an_in_flight_ping_before_its_timeout_and_throws`, `Engine_cancellation_throws_instead_of_reporting_loss`, `Cancelled_token_throws_instead_of_reporting_a_failed_lookup`, and `A_probe_cancelled_mid_flight_writes_nothing_to_the_channel` FAIL (no exception thrown / one item in the channel). `Unknown_host_is_a_failed_result_with_a_reason` passes already.

- [ ] **Step 3: Implement**

`Libraries/ConInfrastructure/PingTransceiverIcmp.cs`: replace the three catch blocks at lines 58-69 with:

```csharp
        catch (PingException ex)
        {
            return new PingResult(_ipAddress, false, 0, $"PingException: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PingResult(_ipAddress, false, 0, $"Error: {ex.Message}");
        }
```

(The engine token's `OperationCanceledException`, rethrown by the inner catch at lines 46-50, now leaves the method. `Ping` reports its own timeout as `IPStatus.TimedOut`, which stays a failed sample.)

`Libraries/ConInfrastructure/TcpConnectTransceiver.cs`: replace lines 40-55 with:

```csharp
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingResult(ipAddress, false, 0, "TimedOut"); // our own timeout token
        }
        catch (SocketException ex)
        {
            return new PingResult(ipAddress, false, 0, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PingResult(ipAddress, false, 0, ex.Message);
        }
```

`Libraries/ConInfrastructure/DomainNameResolverDns.cs`: delete the catch at lines 69-80 (the one filtered on `_cancellationToken.IsCancellationRequested` that returns "Cancelled"); change the timeout catch at line 81 to `catch (OperationCanceledException) when (!_cancellationToken.IsCancellationRequested)`; change the last catch at line 126 to `catch (Exception ex) when (ex is not OperationCanceledException)`. (Task 2.2 rewrites this file; the minimal edit here keeps the task's test cycle honest.)

`Core/ConEngine/Network/PingController.cs`, lines 80-82 become:

```csharp
                    var reply = await ping.PingAsync(_timeoutMsProvider!()).ConfigureAwait(false);
                    if (_cancellationToken.IsCancellationRequested)
                        break; // a reply that raced our stop is not evidence about the host
                    log.LogTrace("Ping result for {ipAddress}: {reply}", ipAddress, reply);
                    SendMessage(reply);
```

Add to the `<summary>` of `IPingTransceiver.PingAsync` (`Core/ConCore/IPingTransceiver.cs:15-21`) and `IDomainNameResolver.ResolveAsync` (`Core/ConCore/IDomainNameResolver.cs:8-14`): "Cancellation of the token given at construction propagates as OperationCanceledException; a timeout is a failed result."

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass (157 + 4 new).

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConInfrastructure Core/ConEngine/Network/PingController.cs Core/ConCore Tests/ConEngine.Tests
git commit -m "fix(infra): cancellation propagates from transceivers and the resolver; a cancelled probe is never a lost sample"
```

---

### Task 2.2: The engine and the Test button surface the real DNS failure reason; the resolver loses its finalizer (decision 6, section 3)

**Files:**
- Modify: `Core/ConEngine/WatcherEngine.cs:280-302`
- Modify: `Programs/ConWatch/Api/ProbeTestEndpoint.cs:21-36, 54-55`
- Rewrite: `Libraries/ConInfrastructure/DomainNameResolverDns.cs`
- Modify: `Tests/ConEngine.Tests/Fakes/FakeDomainNameResolverFactory.cs`
- Test: `Tests/ConEngine.Tests/DnsRefreshTests.cs` (add one test)
- Test: `Tests/ConWatch.Tests/ProbeTestEndpointTests.cs` (add one test)

**Interfaces:**
- Consumes: `DomainResolutionResult.Success`, `.Status` (record unchanged, decision 6).
- Produces: `FakeDomainNameResolverFactory.Failures` (`ConcurrentDictionary<string, string>`: name → status text returned as a *failed result*); `/targets/probe-test` response gains `resolutionError` (string, null on success).

- [ ] **Step 1: Write the failing tests**

In `Tests/ConEngine.Tests/Fakes/FakeDomainNameResolverFactory.cs` add a property next to `Answers`:

```csharp
    /// <summary>Names that resolve to a FAILED result (not an exception) with this status text.</summary>
    public ConcurrentDictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
```

and in `Resolver.ResolveAsync`, after the `ResolveCount` update and before the `Answers` lookup:

```csharp
            if (owner.Failures.TryGetValue(domainName, out var status))
                return Task.FromResult(new DomainResolutionResult(domainName, success: false,
                    Array.Empty<IPAddress>(), status));
```

Add to `Tests/ConEngine.Tests/DnsRefreshTests.cs`:

```csharp
    [Fact]
    public async Task Lookup_failure_reason_reaches_the_transition()
    {
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Failures["site.example"] = "Host not found";
        var engine = new WatcherEngine(TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(HostnameTarget()),
            dnsRefreshInterval: TimeSpan.FromMilliseconds(100));
        var transitions = new List<ConEngine.Events.TargetTransition>();
        engine.TransitionSource.Subscribe(t => { lock (transitions) transitions.Add(t); });
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            await Eventually(() =>
            {
                lock (transitions)
                    return transitions.Any(t =>
                        t.Reason.Trigger == ConEngine.Events.TransitionTrigger.ResolutionFailed &&
                        t.Reason.Detail!.Contains("Host not found"));
            });
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
```

Add to `Tests/ConWatch.Tests/ProbeTestEndpointTests.cs` (use the file's existing `ConWatchFactory`/client fields; if it has none, add `private readonly ConWatchFactory _factory = new(); private readonly HttpClient _client;` with the constructor and `Dispose` pattern of `RuntimeHealthTests.cs:13-18`):

```csharp
    [Fact]
    public async Task Unresolvable_hostname_reports_the_resolver_reason()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/targets/probe-test",
            new { address = "nonexistent.invalid" });
        response.EnsureSuccessStatusCode();
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("resolvedIp").ValueKind);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("resolutionError").GetString()));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~Lookup_failure_reason"` → FAIL (the detail says "No IPv4 address for 'site.example'").
Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Unresolvable_hostname_reports"` → FAIL (`resolutionError` property missing).

- [ ] **Step 3: Implement**

`Core/ConEngine/WatcherEngine.cs`, replace lines 280-302 (the try/catch around the resolve) with:

```csharp
            try
            {
                var result = await resolver.ResolveAsync(hostname, 5000).ConfigureAwait(false);
                answers = result.IPv4Addresses.ToList();
                if (current is null)
                {
                    if (!result.Success)
                        monitor.ReportResolutionFailure($"DNS lookup failed for '{hostname}': {result.Status}");
                    else if (answers.Count == 0)
                        monitor.ReportResolutionFailure($"No IPv4 address for '{hostname}'");
                }
                else if (!result.Success)
                {
                    _log.LogDebug("DNS refresh failed for {Hostname} ({Status}); keeping {Ip}",
                        hostname, result.Status, current);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (current is null)
                {
                    _log.LogWarning(ex, "DNS resolution failed for {Hostname}", hostname);
                    monitor.ReportResolutionFailure($"DNS lookup failed for '{hostname}': {ex.Message}");
                }
                else
                {
                    _log.LogDebug(ex, "DNS refresh failed for {Hostname}; keeping {Ip}", hostname, current);
                }
            }
```

`Programs/ConWatch/Api/ProbeTestEndpoint.cs`, replace lines 21-36 with:

```csharp
            List<IPAddress> answers;
            string? resolutionError = null;
            if (IPAddress.TryParse(request.Address, out var literal))
            {
                answers = [literal];
            }
            else
            {
                using var resolver = resolvers.Create(ct);
                var resolution = await resolver.ResolveAsync(request.Address, 5000);
                answers = resolution.IPv4Addresses.ToList();
                if (!resolution.Success)
                    resolutionError = resolution.Status;
                else if (answers.Count == 0)
                    resolutionError = "The name resolved, but to no IPv4 address.";
            }
            var resolvedIps = answers.Select(a => a.ToString()).ToList();
            var ip = answers.FirstOrDefault();
            if (ip is null)
                return Results.Ok(new { request.Address, ResolvedIp = (string?)null,
                    ResolvedIps = resolvedIps, ResolutionError = resolutionError,
                    Results = Array.Empty<object>() });
```

and change the success return at lines 54-55 to include `ResolutionError = (string?)null` so the shape is stable.

Rewrite `Libraries/ConInfrastructure/DomainNameResolverDns.cs` as:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;

namespace ConInfrastructure;

/// <summary>
/// Domain name resolver over System.Net.Dns. Cancellation of the token given
/// at construction propagates; a timeout or a DNS error is a failed result
/// whose Status says why.
/// </summary>
public sealed class DomainNameResolverDns(CancellationToken cancellationToken) : IDomainNameResolver
{
    public async Task<DomainResolutionResult> ResolveAsync(string domainName, int timeoutMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domainName, timeoutCts.Token).ConfigureAwait(false);
            return new DomainResolutionResult(domainName, success: true, addresses,
                status: "Success", resolutionTimeMs: stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed("Timeout");
        }
        catch (SocketException ex)
        {
            return Failed(ex.SocketErrorCode switch
            {
                SocketError.HostNotFound => "Host not found",
                SocketError.TryAgain => "Temporary DNS failure",
                SocketError.NoRecovery => "Non-recoverable DNS error",
                SocketError.NoData => "No DNS data found",
                _ => $"DNS error: {ex.Message}",
            });
        }
        catch (ArgumentException ex)
        {
            return Failed($"Invalid domain name: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"Error: {ex.Message}");
        }

        DomainResolutionResult Failed(string status) => new(domainName, success: false,
            Array.Empty<IPAddress>(), status, stopwatch.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        // Nothing to release: Dns owns no per-instance resources.
    }
}
```

- [ ] **Step 4: Run the engine and web suites**

Run: `dotnet test Tests/ConEngine.Tests` and `dotnet test Tests/ConWatch.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/WatcherEngine.cs Programs/ConWatch/Api/ProbeTestEndpoint.cs Libraries/ConInfrastructure/DomainNameResolverDns.cs Tests/ConEngine.Tests Tests/ConWatch.Tests/ProbeTestEndpointTests.cs
git commit -m "fix(dns): the real lookup failure reason reaches the transition and the Test button; resolver drops its finalizer"
```

---

### Task 2.3: Ping results are stamped from the engine's clock (E5)

**Files:**
- Modify: `Core/ConEngine/Network/PingController.cs:13, 136-143`
- Modify: `Core/ConEngine/Network/PingManager.cs:34-48, 63-69`
- Modify: `Core/ConEngine/WatcherEngine.cs:187`
- Modify: `Tests/ConEngine.Tests/PingControllerTests.cs` (every `new PingController(...)`), `Tests/ConEngine.Tests/PingManagerTests.cs:35`
- Test: `Tests/ConEngine.Tests/PingControllerTests.cs` (add one test)

**Interfaces:**
- Produces: `PingController(IPingTransceiverFactory pingTransceiverFactory, ILogger log, TimeProvider timeProvider)`; `PingManager(IPingTransceiverFactory, CancellationToken, ILoggerFactory, TimeProvider timeProvider)`. Task 2.4 reads the stamped `Result.Timestamp`.

- [ ] **Step 1: Write the failing test**

Add to `Tests/ConEngine.Tests/PingControllerTests.cs`:

```csharp
    [Fact]
    public async Task Results_carry_the_engine_clock_not_the_wall_clock()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var factory = new FakePingTransceiverFactory();
        var channel = Channel.CreateUnbounded<RoutedPingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(factory, NullLogger.Instance, time);

        _ = controller.Launch(Guid.NewGuid(), IpA, channel.Writer, ProbeSpec.Icmp,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            () => 1000, cts.Token);

        var result = (await Collect(channel.Reader, 1)).Single();
        cts.Cancel();
        Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z"), result.Result.Timestamp);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~Results_carry_the_engine_clock"`
Expected: build FAILS ("no overload for PingController takes 3 arguments").

- [ ] **Step 3: Implement**

`PingController.cs` line 13:

```csharp
public class PingController(IPingTransceiverFactory pingTransceiverFactory, ILogger log,
    TimeProvider timeProvider) : IDisposable
```

`SendMessage` (`:136-143`):

```csharp
    private void SendMessage(PingResult result)
    {
        if (_resultWriter is null)
            return;
        // Stamp from the engine clock: everything downstream (burst-loss windows,
        // rollup minutes, warm-start gaps) is TimeProvider time.
        var stamped = new PingResult(result.IpAddress, result.Success, result.RoundtripTime,
            result.Message, timeProvider.GetUtcNow());
        // If the channel is closed this fails quietly; the manager is shutting down.
        if (!_resultWriter.TryWrite(new RoutedPingResult(_targetId, stamped)))
            log.LogDebug("[{ipAddress}] result dropped: channel closed", _ipAddress);
    }
```

`PingManager.cs`: add `private readonly TimeProvider _timeProvider;`, a fourth constructor parameter `TimeProvider timeProvider` assigned in the constructor body, and at line 66: `controller = new PingController(_pingTransceiverFactory, controllerLogger, _timeProvider);`.

`WatcherEngine.cs:187`: `_manager = new PingManager(_pingFactory, ct, _loggerFactory, _timeProvider);`

Existing tests: every `new PingController(factory, NullLogger.Instance)` in `PingControllerTests.cs` becomes `new PingController(factory, NullLogger.Instance, TimeProvider.System)` (and the `CancelReportingFactory` one from Task 2.1); `PingManagerTests.cs:35` becomes `new PingManager(new HangingFactory(), cts.Token, NullLoggerFactory.Instance, TimeProvider.System)`.

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine Tests/ConEngine.Tests
git commit -m "fix(engine): ping results are stamped from the engine clock, not the wall clock"
```

---
### Task 2.4: The result pump survives a throwing subscriber and records its last result; the DNS follower survives a throw (E1)

**Files:**
- Modify: `Core/ConEngine/Network/PingManager.cs:27, 122-145` (+ new members)
- Modify: `Core/ConEngine/WatcherEngine.cs:212-220, 304-323` (+ new members)
- Test: `Tests/ConEngine.Tests/PingPumpResilienceTests.cs` (new)

**Interfaces:**
- Produces: `PingManager.LastResultUtc` (`DateTimeOffset?`, null until the first routed result), `WatcherEngine.LastResultUtc` (`DateTimeOffset?`, forwards to the manager), `WatcherEngine.StartedUtc` (`DateTimeOffset`, set in `StartAsync`), `WatcherEngine.StallAfter` (`TimeSpan`: the longest configured `NormalInterval` × 3 plus the longest `PingTimeout`, computed in `StartAsync`). Task 2.5 reads all three.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/PingPumpResilienceTests.cs`:

```csharp
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class PingPumpResilienceTests
{
    private static WatcherOptions OneTarget() => new()
    {
        Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
        Targets = [new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Fast" }],
    };

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
            await Task.Delay(20);
        Assert.True(condition(), "Condition not met within timeout");
    }

    [Fact]
    public async Task A_throwing_sample_subscriber_does_not_stop_the_pump()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(OneTarget()));
        int delivered = 0;
        int thrown = 0;
        // Order matters: Rx delivers in subscription order and stops at the first
        // throw, so the counter must be ahead of the thrower to prove the pump
        // kept going AFTER a throw.
        engine.PingSampleSource.Subscribe(_ => Interlocked.Increment(ref delivered));
        engine.PingSampleSource.Subscribe(_ =>
        {
            Interlocked.Increment(ref thrown);
            throw new InvalidOperationException("observer bug");
        });
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            await Eventually(() => Volatile.Read(ref thrown) >= 1);
            var afterFirstThrow = Volatile.Read(ref delivered);
            await Eventually(() => Volatile.Read(ref delivered) >= afterFirstThrow + 5);
            Assert.NotNull(engine.LastResultUtc);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Stall_threshold_follows_the_slowest_profile()
    {
        var options = OneTarget();
        options.Profiles["Slow"] = new PingProfileConfig { NormalIntervalSeconds = 100, PingTimeoutSeconds = 5 };
        options.Targets.Add(new TargetConfig { Name = "Far", Address = "203.0.113.9", Profile = "Slow" });
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(305), engine.StallAfter);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingPumpResilienceTests"`
Expected: build FAILS ("'WatcherEngine' does not contain a definition for 'LastResultUtc'"). After adding the members but before the loop change, `A_throwing_sample_subscriber_does_not_stop_the_pump` FAILS at the second `Eventually` (the pump died on the first throw).

- [ ] **Step 3: Implement**

`Core/ConEngine/Network/PingManager.cs`: add after line 27:

```csharp
    private long _lastResultTicks; // UTC ticks of the last routed result; 0 = none yet

    /// <summary>When the pump last handed a result to its subscribers; null before the first.</summary>
    public DateTimeOffset? LastResultUtc
    {
        get
        {
            var ticks = Volatile.Read(ref _lastResultTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }
```

Replace `ProcessMessages` (`:122-145`) with:

```csharp
    private async Task ProcessMessages()
    {
        try
        {
            _log.LogDebug("PingManager message processing started");
            await foreach (var routed in _resultReader.ReadAllAsync(_globalCancellationToken))
            {
                try
                {
                    LogPingResult(routed.Result);
                    _pingResultSubject.OnNext(routed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Subscribers run inline. One bad observer must not end
                    // result processing for the life of the engine.
                    _log.LogError(ex, "A ping result subscriber threw for target {TargetId}; result skipped",
                        routed.TargetId);
                }
                Volatile.Write(ref _lastResultTicks, routed.Result.Timestamp.UtcTicks);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("PingManager message processing stopped due to cancellation");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error processing messages in PingManager");
        }
        finally
        {
            _pingResultSubject.OnCompleted();
        }
    }
```

`Core/ConEngine/WatcherEngine.cs`: add members next to `Verdict`/`Suspicion`:

```csharp
    /// <summary>When the result pump last delivered a result; null before the first.</summary>
    public DateTimeOffset? LastResultUtc => _manager?.LastResultUtc;

    /// <summary>When StartAsync ran (engine clock).</summary>
    public DateTimeOffset StartedUtc { get; private set; }

    /// <summary>
    /// Silence longer than this from an engine with active monitors means the
    /// pump or every controller is stuck: three normal intervals plus one
    /// timeout of the slowest configured profile.
    /// </summary>
    public TimeSpan StallAfter { get; private set; } = TimeSpan.FromMinutes(5);
```

In `StartAsync`, after `_graph = TargetGraph.FromOptions(_options);` (line 101):

```csharp
        StartedUtc = _timeProvider.GetUtcNow();
        var profiles = _graph.Targets.Values.Where(t => t.Enabled).Select(_graph.ProfileFor).ToList();
        if (profiles.Count > 0)
            StallAfter = profiles.Max(p => p.NormalInterval) * 3 + profiles.Max(p => p.PingTimeout);
```

In `ResolveAndActivateAsync`, wrap the activate/re-point block (lines 304-323) so a throw there logs and the loop continues:

```csharp
            try
            {
                if (current is null)
                {
                    if (answers.Count == 0)
                    {
                        // Not activated yet: retry with backoff until DNS answers.
                        try { await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
                        continue;
                    }
                    current = answers[0];
                    Activate(monitor, current);
                }
                else if (answers.Count > 0 && !answers.Contains(current))
                {
                    _log.LogInformation("{Target}: {Old} left the DNS answer set; re-pointing to {New}",
                        monitor.Target.Name, current, answers[0]);
                    current = answers[0];
                    await RepointAsync(monitor, current).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                return; // the manager stopped under us
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "{Target}: activating/re-pointing failed; will retry at the next refresh",
                    monitor.Target.Name);
            }
```

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine Tests/ConEngine.Tests/PingPumpResilienceTests.cs
git commit -m "fix(engine): the result pump survives a throwing subscriber and stamps its last result; DNS follower survives a throw"
```

---

### Task 2.5: `/health` moves to StatusEndpoints, detects a stalled engine, and stops hitting the database per call (S10, E1 part 2, H6 part)

**Files:**
- Create: `Programs/ConWatch/EngineHealth.cs`
- Modify: `Programs/ConWatch/Api/StatusEndpoints.cs`
- Modify: `Programs/ConWatch/Program.cs:73-90` (delete the inline `/health`)
- Modify: `Programs/ConWatch/WatcherHostService.cs:49-51, 113-124, 154`
- Modify: `Programs/ConWatch/Api/MetricsEndpoint.cs:19-24, 33-35`
- Test: `Tests/ConWatch.Tests/EngineHealthTests.cs` (new)
- Test: `Tests/ConWatch.Tests/HostSmokeTests.cs:34` (extend the DTO)

**Interfaces:**
- Consumes: `WatcherEngine.LastResultUtc`, `StartedUtc`, `StallAfter` (Task 2.4).
- Produces: `WatcherHostService.EngineEnabled` (`bool`, set by `StartEngineAsync`, defaults true); `EngineHealth.Classify(WatcherEngine? engine, bool enabled, DateTimeOffset now)` returning `EngineHealth.Verdict(string Status, bool Stalled, TimeSpan? SilenceFor)`; `/health` body gains `stalled` (bool) and `silenceSeconds` (number|null); status `EngineStalled` (503). `/metrics` gains `conwatch_last_sample_age_seconds` (gauge; absent before the first sample).

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConWatch.Tests/EngineHealthTests.cs`:

```csharp
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConWatch.Tests;

public class EngineHealthTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-18T12:00:00Z");

    private static WatcherOptions OneTarget() => new()
    {
        Profiles = { ["P"] = new PingProfileConfig { NormalIntervalSeconds = 10, PingTimeoutSeconds = 3 } },
        Targets = [new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "P" }],
    };

    /// <summary>A fake clock keeps every controller asleep, so the engine never
    /// produces a result: a clean way to get "started, active, silent".</summary>
    private static async Task<(ConEngine.WatcherEngine Engine, FakeTimeProvider Time, CancellationTokenSource Cts)> SilentEngineAsync()
    {
        var time = new FakeTimeProvider(T0);
        var engine = new ConEngine.WatcherEngine(time, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(OneTarget()));
        var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        return (engine, time, cts);
    }

    [Fact]
    public void No_engine_is_EngineDown_when_enabled_and_Disabled_otherwise()
    {
        Assert.Equal("EngineDown", EngineHealth.Classify(null, enabled: true, T0).Status);
        Assert.Equal("Disabled", EngineHealth.Classify(null, enabled: false, T0).Status);
    }

    [Fact]
    public async Task Fresh_engine_is_healthy_until_the_stall_window_passes_with_no_result()
    {
        var (engine, time, cts) = await SilentEngineAsync();
        try
        {
            Assert.Equal("Healthy", EngineHealth.Classify(engine, true, time.GetUtcNow()).Status);

            time.Advance(engine.StallAfter + TimeSpan.FromSeconds(1)); // 3*10 + 3 = 33 s window
            var verdict = EngineHealth.Classify(engine, true, time.GetUtcNow());
            Assert.Equal("EngineStalled", verdict.Status);
            Assert.True(verdict.Stalled);
            Assert.Equal(TimeSpan.FromSeconds(34), verdict.SilenceFor);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task Engine_with_no_monitors_never_stalls()
    {
        var time = new FakeTimeProvider(T0);
        var engine = new ConEngine.WatcherEngine(time, new FakePingTransceiverFactory(),
            new FakeDomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(new WatcherOptions()));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            time.Advance(TimeSpan.FromHours(1));
            Assert.Equal("Healthy", EngineHealth.Classify(engine, true, time.GetUtcNow()).Status);
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
```

In `Tests/ConWatch.Tests/HostSmokeTests.cs:34` extend the DTO and add an assertion:

```csharp
    private sealed record HealthDto(string Status, bool EngineRunning, int TargetCount, bool Stalled);
```

and after `Assert.Equal(0, health.TargetCount);`: `Assert.False(health.Stalled);`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~EngineHealthTests|FullyQualifiedName~HostSmokeTests"`
Expected: build FAILS ("The name 'EngineHealth' does not exist").

- [ ] **Step 3: Implement**

Create `Programs/ConWatch/EngineHealth.cs`:

```csharp
using ConEngine;

namespace ConWatch;

/// <summary>
/// One place that turns engine state into a health verdict. A monitor whose
/// job is to notice silence must notice its own: an engine with active
/// monitors that has delivered no result for longer than its stall window
/// is "EngineStalled", not "Healthy".
/// </summary>
public static class EngineHealth
{
    public sealed record Verdict(string Status, bool Stalled, TimeSpan? SilenceFor);

    public static Verdict Classify(WatcherEngine? engine, bool enabled, DateTimeOffset now)
    {
        if (engine is null)
            return new Verdict(enabled ? "EngineDown" : "Disabled", Stalled: false, SilenceFor: null);

        // Only monitors that have an address are expected to produce results;
        // a hostname still resolving is not silence.
        var anyActive = engine.Monitors.Values.Any(m => m.ResolvedAddress is not null);
        if (!anyActive)
            return new Verdict("Healthy", Stalled: false, SilenceFor: null);

        var since = engine.LastResultUtc ?? engine.StartedUtc;
        var silence = now - since;
        return silence > engine.StallAfter
            ? new Verdict("EngineStalled", Stalled: true, SilenceFor: silence)
            : new Verdict("Healthy", Stalled: false, SilenceFor: silence);
    }
}
```

`Programs/ConWatch/WatcherHostService.cs`: add after `EngineRunning` (line 51):

```csharp
    /// <summary>The master enable switch as last read by StartEngineAsync; the
    /// health, status and metrics endpoints read this instead of the database.</summary>
    public bool EngineEnabled { get; private set; } = true;
```

In `StartEngineAsync` replace lines 115-116 (`if (!await EngineEnabledAsync())` and its brace) with:

```csharp
        EngineEnabled = await EngineEnabledAsync();
        if (!EngineEnabled)
        {
```

`Programs/ConWatch/Api/StatusEndpoints.cs`: replace the file with:

```csharp
namespace ConWatch.Api;

/// <summary>Live status and health from the running engine.</summary>
public static class StatusEndpoints
{
    public static void MapStatus(this RouteGroupBuilder api)
    {
        api.MapGet("/status", (WatcherHostService host) =>
        {
            if (StatusSnapshotBuilder.Build(host.Engine) is { } snapshot)
                return Results.Ok(snapshot);
            // Archive mode is a healthy answer, not an error: no live states.
            if (!host.EngineEnabled)
                return Results.Ok(new
                {
                    Enabled = false,
                    Verdict = (object?)null,
                    Targets = Array.Empty<object>(),
                    Pools = Array.Empty<object>(),
                });
            return Results.Json(new { error = "Engine is not running" }, statusCode: 503);
        });

        api.MapGet("/health", (WatcherHostService host, TimeProvider time) =>
        {
            var engine = host.Engine;
            var now = time.GetUtcNow();
            var verdict = EngineHealth.Classify(engine, host.EngineEnabled, now);
            var body = new
            {
                // A deliberately disabled node is a healthy archive (200) — container
                // healthchecks must not flag it; 503 stays for enabled-but-down and stalled.
                Status = verdict.Status,
                Enabled = host.EngineEnabled,
                EngineRunning = engine is not null,
                Stalled = verdict.Stalled,
                SilenceSeconds = verdict.SilenceFor?.TotalSeconds,
                TargetCount = engine?.Monitors.Count ?? 0,
                TimestampUtc = now,
                Version = BuildInfo.Version,
                BuiltUtc = BuildInfo.BuiltUtc,
            };
            var ok = verdict.Status is "Healthy" or "Disabled";
            return ok ? Results.Ok(body) : Results.Json(body, statusCode: 503);
        });
    }
}
```

`Programs/ConWatch/Program.cs`: delete lines 73-90 (the inline `/health`).

`Programs/ConWatch/Api/MetricsEndpoint.cs`: line 19-24 become

```csharp
        app.MapGet("/metrics", (WatcherHostService host, MetricsCounters counters, TimeProvider time) =>
            Results.Text(Render(host.Engine, counters, host.EngineEnabled, time.GetUtcNow()),
                "text/plain; version=0.0.4", Encoding.UTF8));

    internal static string Render(WatcherEngine? engine, MetricsCounters counters, bool enabled, DateTimeOffset now)
```

and after the `conwatch_engine_enabled` family (line 34) add:

```csharp
        if (engine?.LastResultUtc is { } last)
        {
            Family(sb, "conwatch_last_sample_age_seconds", "gauge", "Seconds since the engine last processed a ping result.");
            sb.Append("conwatch_last_sample_age_seconds ")
              .Append((now - last).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }
```

Fix any other caller of `MetricsEndpoint.Render` (grep `Render(` in `Tests/ConWatch.Tests`; pass `DateTimeOffset.UtcNow` there).

- [ ] **Step 4: Run the web suite**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass (`MasterEnableTests` still sees "Healthy"/"Disabled"; `ApiSurfaceLockTests` may list `/health` under a new file: update its expected map if it pins routes to files).

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch Tests/ConWatch.Tests
git commit -m "feat(health): /health detects a stalled engine, lives with /status, and no longer reads the database per call"
```

---

### Task 2.6: Coordinators classify from one snapshot of member states; the verdict exposes one immutable snapshot (E3, refactor under existing tests)

**Files:**
- Modify: `Core/ConEngine/Topology/PoolCoordinator.cs:60-100`
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs:41-46, 109-206`
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs:16-23`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs:44-49`, `Libraries/ConNotify/MqttNotifier.cs:175-182`
- Test: `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs` (add one test)

**Interfaces:**
- Produces: `VerdictCoordinator.VerdictSnapshot(VerdictState State, string? Detail, string? Operator)` (public sealed record) and `VerdictCoordinator.Current` (`VerdictSnapshot`, one reference swapped under the lock). `CurrentVerdict`, `CurrentDetail`, `CurrentOperator` stay as forwarding properties so every existing caller and test compiles.

The publish-outside-the-lock half of E3 (a per-coordinator outbox like `TargetMonitor`'s) is deferred to plan B; this task removes the second-read problem only.

- [ ] **Step 1: Write the test for the snapshot record**

Add to `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs`:

```csharp
    [Fact]
    public void Current_is_one_snapshot_whose_detail_matches_its_state()
    {
        Feed("Google", 5, true); Feed("Cloudflare", 5, true); Feed("Quad9", 5, true);
        Feed("Modem", 5, true); Feed("Router", 5, true);
        var healthy = _verdict.Current;
        Assert.Equal(VerdictState.Healthy, healthy.State);
        Assert.StartsWith("Healthy", healthy.Detail);

        Feed("Modem", 10, false);
        var down = _verdict.Current;
        Assert.Equal(VerdictState.LinkDown, down.State);
        Assert.StartsWith("LinkDown", down.Detail);
        Assert.Equal("Comcast", down.Operator);
        // The earlier snapshot is untouched: readers holding it never see a torn pair.
        Assert.Equal(VerdictState.Healthy, healthy.State);
    }
```

(`Feed` is the fixture's existing helper at `VerdictCoordinatorTests.cs:57`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~Current_is_one_snapshot"`
Expected: build FAILS ("does not contain a definition for 'Current'").

- [ ] **Step 3: Implement**

`VerdictCoordinator.cs`: replace lines 41-46 with:

```csharp
    /// <summary>State, detail and blamed operator as one immutable value:
    /// readers on other threads get a consistent triple, never a torn one.</summary>
    public sealed record VerdictSnapshot(VerdictState State, string? Detail, string? Operator);

    public VerdictSnapshot Current { get; private set; } = new(VerdictState.Unknown, null, null);

    public VerdictState CurrentVerdict => Current.State;
    public string? CurrentDetail => Current.Detail;
    /// <summary>The ISP/operator being blamed by a Link* verdict (from the
    /// blamed trunk target's Operator tag), else null.</summary>
    public string? CurrentOperator => Current.Operator;
```

In `Seed` (`:84-93`): `CurrentVerdict = verdict;` becomes `Current = new VerdictSnapshot(verdict, null, null);`.

Replace `Recalculate` (`:109-141`) with:

```csharp
    private void Recalculate(bool ignoreUnknown = false)
    {
        lock (_lock)
        {
            // One read per monitor: classification, detail and blame all come
            // from the same snapshot, so the detail can never contradict the state.
            var anchors = _anchors.Select(a => (Monitor: a, State: a.CurrentState)).ToList();
            var ancestors = _ancestors.Select(a => (a.Monitor, a.Depth, State: a.Monitor.CurrentState)).ToList();
            var (verdict, detail, operatorName) = Classify(anchors, ancestors);
            var previous = Current;
            if (verdict == previous.State)
            {
                Current = previous with { Detail = detail, Operator = operatorName }; // detail carries live counts — always refresh
                return;
            }
            if (ignoreUnknown && verdict == VerdictState.Unknown)
                return; // a seeded verdict outranks "no anchors determined yet"

            Current = new VerdictSnapshot(verdict, detail, operatorName);
            var transition = new TargetTransition
            {
                Kind = TransitionSubjectKind.Verdict,
                TargetId = SubjectId,
                TargetName = "Internet",
                From = Map(previous.State),
                To = Map(verdict),
                Timestamp = _time.GetUtcNow(),
                Reason = new TransitionReason
                {
                    Trigger = TransitionTrigger.VerdictChanged,
                    Verdict = verdict.ToString(),
                    Detail = detail,
                },
            };
            _log.LogInformation("Verdict: {Transition}", transition);
            _transitionSubject.OnNext(transition);
        }
    }
```

Replace `Classify` (`:143-199`) with a snapshot-driven version:

```csharp
    private (VerdictState, string, string? Operator) Classify(
        IReadOnlyList<(TargetMonitor Monitor, HostState State)> anchors,
        IReadOnlyList<(TargetMonitor Monitor, int Depth, HostState State)> ancestors)
    {
        // Link verdicts name the blamed hop's operator ("Comcast") when tagged.
        static (VerdictState, string, string?) Blame(VerdictState verdict, TargetMonitor monitor, string state)
        {
            var op = monitor.Target.Operator;
            var wording = op is { Length: > 0 }
                ? $"{verdict}: {op} — {monitor.Target.Name} is {state}"
                : $"{verdict}: {monitor.Target.Name} is {state}";
            return (verdict, wording, op);
        }

        var downAncestor = ancestors.Where(a => a.State == HostState.Down)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (downAncestor.Monitor is not null)
            return Blame(VerdictState.LinkDown, downAncestor.Monitor, "Down");

        var faltAncestor = ancestors.Where(a => a.State == HostState.Faltering)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (faltAncestor.Monitor is not null)
            return Blame(VerdictState.LinkDegraded, faltAncestor.Monitor, "Faltering");

        var determined = anchors.Where(a => a.State
            is not (HostState.Initializing or HostState.Starting)).ToList();
        if (determined.Count == 0)
            return (VerdictState.Unknown, "No anchors determined yet", null);

        var degraded = determined.Where(a => a.State
            is HostState.Down or HostState.Faltering or HostState.Recovering).ToList();
        string List() => string.Join(", ", degraded.Select(a => $"{a.Monitor.Target.Name}={a.State}"));

        // Weather needs INDEPENDENT evidence. Anchors that share an operator
        // ("Microsoft" four times, one per A record) share a path and fail
        // together, so they count once; untagged anchors count individually.
        var determinedUnits = CountOperators(determined.Select(a => a.Monitor));
        var degradedUnits = CountOperators(degraded.Select(a => a.Monitor));
        var grouped = degradedUnits != degraded.Count || determinedUnits != determined.Count;

        // With fewer independent anchors than the weather threshold, weather can
        // never be diagnosed — say so rather than implying full confidence.
        var qualifier = determinedUnits < _weatherThreshold
            ? $" (only {determinedUnits} {(grouped ? "operator(s)" : "anchor(s)")} — cannot distinguish a site incident from internet weather)"
            : "";

        if (degraded.Count == 0)
            return (VerdictState.Healthy, $"Healthy: {determined.Count} anchors responding{qualifier}", null);
        if (degradedUnits < _weatherThreshold)
        {
            var sharedOperator = degraded.Count > 1 && degradedUnits == 1
                ? $" — all {degraded[0].Monitor.Target.Operator}" : "";
            return (VerdictState.SiteIncident, $"SiteIncident: {List()}{sharedOperator}{qualifier}", null);
        }
        var counts = grouped
            ? $"{degradedUnits} of {determinedUnits} operators ({degraded.Count} of {determined.Count} anchors)"
            : $"{degraded.Count} of {determined.Count} anchors";
        return (VerdictState.InternetWeather, $"InternetWeather: {counts} degraded ({List()})", null);
    }
```

(`CountOperators` at `:202-206` is unchanged.)

`PoolCoordinator.Recalculate` (`:60-100`): take the snapshot once and build the detail from it:

```csharp
    private void Recalculate()
    {
        lock (_lock)
        {
            var members = _members.Select(m => (m.Target.Name, State: m.CurrentState)).ToList();
            var states = members.Select(m => m.State).ToList();

            HostState next;
            if (states.All(s => s is HostState.Initializing or HostState.Starting))
                next = HostState.Starting;
            else if (states.Any(s => s == HostState.Responding))
                next = HostState.Responding;
            else if (states.All(s => s is HostState.Down or HostState.Unreachable))
                next = HostState.Down;
            else
                next = HostState.Faltering;

            if (next == CurrentState)
                return;

            var from = CurrentState;
            CurrentState = next;
            var detail = string.Join(", ", members.Select(m => $"{m.Name}={m.State}"));
```

(the rest of the method is unchanged).

Readers: `StatusSnapshotBuilder.cs:16-23` reads `var current = v.Current;` once and uses `current.State.ToString()`, `current.Detail`, `current.Operator`; `ConWatchMcpTools.cs:44-49` the same; `MqttNotifier.cs:175-182` reads `var current = engine.Verdict?.Current;` once and uses `current?.State.ToString() ?? "Unknown"`, `current?.Detail`, `current?.Operator`.

- [ ] **Step 4: Run the engine, web and notify suites**

Run: `dotnet test ConnectionWatcher.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/Topology Programs/ConWatch/StatusSnapshotBuilder.cs Programs/ConWatch/Mcp/ConWatchMcpTools.cs Libraries/ConNotify/MqttNotifier.cs Tests/ConEngine.Tests/VerdictCoordinatorTests.cs
git commit -m "refactor(engine): coordinators classify from one snapshot; verdict exposes an immutable state/detail/operator triple"
```

---

### Task 2.7: Sweep cooldown removed; thresholds must be ordered (E4, E6)

**Files:**
- Modify: `Core/ConEngine/Topology/SuspicionCoordinator.cs:19, 52, 156-164`
- Modify: `Core/ConEngine/Configuration/WatcherOptionsValidator.cs:93-111`
- Test: `Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs` (add two tests)

- [ ] **Step 1: Write the failing validator tests**

Add to `Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs`:

```csharp
    [Fact]
    public void Responding_threshold_below_faltering_threshold_fails()
    {
        var o = ValidHome();
        o.Profiles["Flappy"] = new PingProfileConfig { RespondingThreshold = 0.5, FalteringThreshold = 0.75 };
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("FalteringThreshold", result.FailureMessage);
    }

    [Fact]
    public void Recovering_threshold_below_down_threshold_fails()
    {
        var o = ValidHome();
        o.Profiles["Flappy"] = new PingProfileConfig { DownThreshold = 0.3, RecoveringThreshold = 0.25 };
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("RecoveringThreshold", result.FailureMessage);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~threshold"`
Expected: both FAIL (`result.Failed` is false).

- [ ] **Step 3: Implement**

`WatcherOptionsValidator.cs`, inside the profile loop after the `[0, 1]` checks (line 108):

```csharp
            if (p.FalteringThreshold >= p.RespondingThreshold)
                failures.Add($"Profile '{profileName}': FalteringThreshold ({p.FalteringThreshold}) must be below RespondingThreshold ({p.RespondingThreshold}), or the state flaps on every sample between them.");
            if (p.DownThreshold >= p.RecoveringThreshold)
                failures.Add($"Profile '{profileName}': DownThreshold ({p.DownThreshold}) must be below RecoveringThreshold ({p.RecoveringThreshold}).");
```

`SuspicionCoordinator.cs`: delete line 19 (`SweepCooldown`), line 52 (`_lastSweep`), and lines 158-161 in `Sweep()` (the cooldown check and `_lastSweep = now`), leaving:

```csharp
    /// <summary>Bursts every anchor and trunk target. Rate limiting is the
    /// discipline's job: PingDiscipline.SuspicionGuard refuses a new burst
    /// within 60 s of the last one on that target (owner decision 2026-09-18).</summary>
    private void Sweep()
    {
        foreach (var monitor in _sweepSet)
            monitor.Discipline.TriggerBurst();
    }
```

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass (`SuspicionCoordinatorTests` are unchanged: they advance 120 s between sweeps).

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs
git commit -m "fix(engine): validator rejects misordered thresholds; the discipline guard is the only sweep rate limit"
```

---
## Phase 3: Persistence

### Task 3.1: A recorder retries its pending batch on a timer, logs channel drops, and only ends on host stop (P1, P7)

**Files:**
- Modify: `Libraries/ConPersistence/Services/ChannelRecorder.cs:14-19, 28-39, 41-64`
- Modify: `Libraries/ConPersistence/Services/TransitionRecorder.cs:9-10`, `Libraries/ConPersistence/Services/GlitchRecorder.cs:14-15`
- Modify: `Libraries/ConPersistence/Services/RollupCollector.cs:49`, `Libraries/ConPersistence/Services/RetentionService.cs:56`
- Test: `Tests/ConPersistence.Tests/RecorderCollectorTests.cs` (add two tests)

**Interfaces:**
- Produces: `ChannelRecorder<TEvent>(ILogger log, int capacity = 1000, int batchSize = 500, TimeSpan? retryDelay = null)`; `TransitionRecorder(IDbContextFactory<ConWatchDbContext> dbFactory, ILogger<TransitionRecorder> log, TimeSpan? retryDelay = null, int capacity = 1000)`; `GlitchRecorder` gains the same two optional parameters. Production callers (DI) pass nothing.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ConPersistence.Tests/RecorderCollectorTests.cs`:

```csharp
    [Fact]
    public async Task Background_loop_retries_a_failed_batch_without_waiting_for_another_event()
    {
        var recorder = new TransitionRecorder(new FailingOnceDbFactory(_factory),
            NullLogger<TransitionRecorder>.Instance, retryDelay: TimeSpan.FromMilliseconds(50));
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        await recorder.StartAsync(CancellationToken.None); // runs ExecuteAsync

        source.OnNext(Transition(TransitionSubjectKind.Target, "Google", HostState.Down));

        // One event, one failed write, no second event: the row must still land.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await using var check = _factory.CreateDbContext();
            if (await check.Transitions.CountAsync() == 1)
                break;
            await Task.Delay(25);
        }
        await recorder.StopAsync(CancellationToken.None);

        await using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Transitions.CountAsync());
    }

    [Fact]
    public async Task A_full_channel_drops_the_oldest_and_keeps_the_newest()
    {
        var recorder = new TransitionRecorder(_factory, NullLogger<TransitionRecorder>.Instance, capacity: 2);
        using var source = new Subject<TargetTransition>();
        recorder.Attach(source);
        source.OnNext(Transition(TransitionSubjectKind.Target, "First", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Second", HostState.Down));
        source.OnNext(Transition(TransitionSubjectKind.Target, "Third", HostState.Down));

        await recorder.FlushAsync();

        await using var db = _factory.CreateDbContext();
        var names = await db.Transitions.Select(t => t.SubjectName).OrderBy(n => n).ToListAsync();
        Assert.Equal(["Second", "Third"], names); // DropOldest: the newest row is what warm start seeds from
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~Background_loop_retries|FullyQualifiedName~A_full_channel_drops"`
Expected: build FAILS (no `retryDelay`/`capacity` parameters on `TransitionRecorder`). After adding the parameters but before the loop change, `Background_loop_retries_a_failed_batch_without_waiting_for_another_event` FAILS with count 0.

- [ ] **Step 3: Implement**

`ChannelRecorder.cs` lines 14-19:

```csharp
public abstract class ChannelRecorder<TEvent>(ILogger log, int capacity = 1000, int batchSize = 500,
    TimeSpan? retryDelay = null) : BackgroundService
{
    // DropOldest is the right policy for transitions: the NEWEST row per subject
    // is what warm start seeds from, so under a long database outage the recent
    // past matters more than the distant. TryWrite never reports the eviction
    // (it returns false only for a completed channel), so the drop is logged here.
    private readonly Channel<TEvent> _channel = Channel.CreateBounded<TEvent>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest },
        // A field initializer cannot touch instance members (no `this` yet), so
        // the message names the event, whose ToString says which kind it is.
        dropped => log.LogWarning("Recorder channel full; dropped {Event}", dropped));
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);
    private List<TEvent>? _pending; // a batch whose write failed; retried first
    private IDisposable? _subscription;

    /// <summary>Plural noun for log lines ("transitions", "glitches").</summary>
    protected abstract string EventNoun { get; }
```

`Attach` (`:28-39`) loses the warning:

```csharp
    public void Attach(IObservable<TEvent> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            e => _channel.Writer.TryWrite(e),
            ex => log.LogError(ex, "{Noun} stream error", EventNoun));
    }
```

Replace `ExecuteAsync` (`:41-64`) with:

```csharp
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                // With a batch still pending, do not wait for a NEW event: the
                // retry is due on the timer, not on the next transition (which
                // on a quiet link can be hours away).
                if (_pending is null && !await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                    break;
                try
                {
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Failed writing {Noun}; retrying in {Delay}", EventNoun, _retryDelay);
                    await Task.Delay(_retryDelay, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: drain what's left without a token.
            try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Final {Noun} flush failed", EventNoun); }
        }
    }
```

`TransitionRecorder.cs:9-10`:

```csharp
public class TransitionRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<TransitionRecorder> log, TimeSpan? retryDelay = null, int capacity = 1000)
    : ChannelRecorder<TargetTransition>(log, capacity, retryDelay: retryDelay)
```

`GlitchRecorder.cs:14-15` the same shape with `ChannelRecorder<GlitchEvent>(log, capacity, retryDelay: retryDelay)`.

`RollupCollector.cs:49` and `RetentionService.cs:56`: `catch (OperationCanceledException)` becomes `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)`.

Also fix the stale comment at `ChannelRecorder.cs:53` (it now prints the real delay) and note in the class summary that a failed batch is retried on a timer.

- [ ] **Step 4: Run the persistence suite**

Run: `dotnet test Tests/ConPersistence.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConPersistence/Services Tests/ConPersistence.Tests/RecorderCollectorTests.cs
git commit -m "fix(persistence): recorders retry a pending batch on the timer, log channel evictions, end only on host stop"
```

---

### Task 3.2: Rollup collector coalesces duplicate pending buckets, looks rows up in one query, takes buckets under its lock, and caps a poisoned pending list (P2, P6, section 3 N+1)

**Files:**
- Modify: `Libraries/ConPersistence/Rollups/RollupAccumulator.cs:6-19`
- Modify: `Libraries/ConPersistence/Services/RollupCollector.cs:15-20, 57-100`
- Test: `Tests/ConPersistence.Tests/FailingNTimesDbFactory.cs` (new)
- Test: `Tests/ConPersistence.Tests/RecorderCollectorTests.cs` (add two tests)

**Interfaces:**
- Produces: `RollupBucket.Absorb(RollupBucket other)`; `RollupCollector(IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider timeProvider, ILogger<RollupCollector> log, int maxConsecutiveFailures = 20)`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConPersistence.Tests/FailingNTimesDbFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Fails the first N context creations, then delegates.</summary>
public sealed class FailingNTimesDbFactory(IDbContextFactory<ConWatchDbContext> inner, int failures)
    : IDbContextFactory<ConWatchDbContext>
{
    private int _calls;

    public ConWatchDbContext CreateDbContext()
    {
        if (Interlocked.Increment(ref _calls) <= failures)
            throw new InvalidOperationException("simulated SQLITE_BUSY");
        return inner.CreateDbContext();
    }

    public Task<ConWatchDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref _calls) <= failures)
            throw new InvalidOperationException("simulated SQLITE_BUSY");
        return inner.CreateDbContextAsync(cancellationToken);
    }
}
```

Add to `RecorderCollectorTests.cs`:

```csharp
    [Fact]
    public async Task A_late_sample_for_a_minute_still_pending_after_a_failed_write_merges_instead_of_poisoning()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(new FailingOnceDbFactory(_factory), time, NullLogger<RollupCollector>.Instance);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        var minute0 = DateTimeOffset.Parse("2026-09-01T00:00:00Z");

        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: minute0.AddSeconds(5))));
        time.Advance(TimeSpan.FromSeconds(65)); // minute 0 closed
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushClosedAsync());

        // A reply stamped inside minute 0 consumed after the tick that closed it:
        // a SECOND bucket with the same key joins the pending one.
        source.OnNext((target, new PingResult(Ip, true, 30, timestamp: minute0.AddSeconds(59))));
        time.Advance(TimeSpan.FromSeconds(60));
        await collector.FlushClosedAsync(); // used to hit the UNIQUE index forever

        await using var db = _factory.CreateDbContext();
        var row = await db.Rollups.SingleAsync(r => r.TargetId == target);
        Assert.Equal(2, row.PingCount);
        Assert.Equal(2, row.SuccessCount);
        Assert.Equal(20.0, row.RttAvg);
        Assert.Equal(30, row.RttMax);
    }

    [Fact]
    public async Task Pending_buckets_are_dropped_after_too_many_consecutive_failures()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
        var collector = new RollupCollector(new FailingNTimesDbFactory(_factory, failures: 2), time,
            NullLogger<RollupCollector>.Instance, maxConsecutiveFailures: 2);
        using var source = new Subject<(Guid, PingResult)>();
        collector.Attach(source);
        var target = Guid.NewGuid();
        source.OnNext((target, new PingResult(Ip, true, 10, timestamp: time.GetUtcNow())));

        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushAllAsync()); // 1st failure
        await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushAllAsync()); // 2nd: cap hit, dropped
        await collector.FlushAllAsync(); // database works again; nothing left to write

        await using var db = _factory.CreateDbContext();
        Assert.Equal(0, await db.Rollups.CountAsync());
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~A_late_sample_for_a_minute|FullyQualifiedName~Pending_buckets_are_dropped"`
Expected: the first FAILS with a `DbUpdateException` (UNIQUE constraint) on the second flush; the second fails to build (`maxConsecutiveFailures`).

- [ ] **Step 3: Implement**

`RollupAccumulator.cs`, inside `RollupBucket` after `EffectiveMin`:

```csharp
    /// <summary>Folds another bucket for the same target and minute into this one.</summary>
    public void Absorb(RollupBucket other)
    {
        PingCount += other.PingCount;
        if (other.SuccessCount > 0)
        {
            RttSum += other.RttSum;
            RttMin = Math.Min(RttMin, other.RttMin);
            RttMax = Math.Max(RttMax, other.RttMax);
            SuccessCount += other.SuccessCount;
        }
    }
```

`RollupCollector.cs`: class header and fields (`:15-20`):

```csharp
public class RollupCollector(IDbContextFactory<ConWatchDbContext> dbFactory,
    TimeProvider timeProvider, ILogger<RollupCollector> log,
    int maxConsecutiveFailures = 20) : BackgroundService
{
    private readonly RollupAccumulator _accumulator = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private List<RollupBucket> _pending = new();
    private int _consecutiveFailures;
    private IDisposable? _subscription;
```

Replace lines 57-100 (`FlushClosedAsync`, `FlushAllAsync`, `WriteAsync`) with:

```csharp
    /// <summary>Writes buckets whose minute has closed.</summary>
    public Task FlushClosedAsync(CancellationToken ct = default) =>
        WriteAsync(() => _accumulator.TakeClosed(timeProvider.GetUtcNow().UtcDateTime), ct);

    /// <summary>Writes every bucket including the open minute (tests / take-over final sync).</summary>
    public Task FlushAllAsync(CancellationToken ct = default) =>
        WriteAsync(() => _accumulator.TakeAll(), ct);

    /// <summary>
    /// Serialized: the timer loop and a caller-driven flush (take-over, tests)
    /// must not interleave. Buckets are taken from the accumulator only once
    /// the lock is held, so a cancelled wait cannot strand them. Pending
    /// buckets for the same target and minute (a late sample after a failed
    /// write) are merged before the write, so a retry can never collide with
    /// itself on the unique index. After too many consecutive failures the
    /// pending list is dropped with an error rather than growing forever.
    /// </summary>
    private async Task WriteAsync(Func<List<RollupBucket>> take, CancellationToken ct)
    {
        await _flushLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _pending.AddRange(take());
            if (_pending.Count == 0)
                return;
            ct.ThrowIfCancellationRequested();

            var merged = Coalesce(_pending);
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var earliest = merged.Min(b => b.PeriodStartUtc);
            var targetIds = merged.Select(b => b.TargetId).Distinct().ToList();
            var existing = await db.Rollups
                .Where(r => r.Resolution == "Minute" && r.PeriodStartUtc >= earliest && targetIds.Contains(r.TargetId))
                .ToListAsync(ct).ConfigureAwait(false);
            var byKey = existing.ToDictionary(r => (r.TargetId, r.PeriodStartUtc));
            foreach (var bucket in merged)
            {
                var row = RollupMerge.ToRow(bucket);
                if (byKey.TryGetValue((row.TargetId, row.PeriodStartUtc), out var found))
                    RollupMerge.Combine(found, row);
                else
                    db.Rollups.Add(row);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            log.LogDebug("Wrote {Count} rollup buckets", merged.Count);
            _pending = new();
            _consecutiveFailures = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (++_consecutiveFailures >= maxConsecutiveFailures)
            {
                log.LogError(ex, "Dropping {Count} pending rollup buckets after {Failures} consecutive failed writes",
                    _pending.Count, _consecutiveFailures);
                _pending = new();
                _consecutiveFailures = 0;
            }
            throw;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private static List<RollupBucket> Coalesce(List<RollupBucket> buckets)
    {
        var byKey = new Dictionary<(Guid, DateTime), RollupBucket>();
        foreach (var bucket in buckets)
        {
            if (byKey.TryGetValue((bucket.TargetId, bucket.PeriodStartUtc), out var first))
                first.Absorb(bucket);
            else
                byKey[(bucket.TargetId, bucket.PeriodStartUtc)] = bucket;
        }
        return byKey.Values.ToList();
    }
```

- [ ] **Step 4: Run the persistence suite**

Run: `dotnet test Tests/ConPersistence.Tests`
Expected: all pass, including `Collector_writes_closed_minutes_and_merges_duplicates` and `A_failed_rollup_write_is_retried_next_flush`.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConPersistence/Rollups/RollupAccumulator.cs Libraries/ConPersistence/Services/RollupCollector.cs Tests/ConPersistence.Tests
git commit -m "fix(persistence): rollup collector merges duplicate pending buckets, one lookup per flush, bounded pending list"
```

---

### Task 3.3: An outage open at the window start counts in the report and on the History page (P3)

**Files:**
- Modify: `Libraries/ConPersistence/Report/IspReportBuilder.cs:192-203, 220-266`
- Modify: `Programs/ConWatch/ClientApp/src/lib/historyData.ts:15-64`
- Modify: `Programs/ConWatch/ClientApp/src/history.tsx:127`
- Test: `Tests/ConPersistence.Tests/IspReportBuilderTests.cs` (add two tests)
- Test: `Programs/ConWatch/ClientApp/src/lib/historyData.test.ts` (update calls, add one test)

**Interfaces:**
- Produces: `historyData.incidents(transitions, rangeStartIso, rangeEndIso)` (three arguments; was two). The page derives the state at the range start from the first in-range transition's `from`, exactly as `ribbonSegments` already does; with no in-range transition the counters stay zero (the API sends no pre-window row; noted as a limitation in the function comment).

- [ ] **Step 1: Write the failing report tests**

Add to `Tests/ConPersistence.Tests/IspReportBuilderTests.cs`:

```csharp
    [Fact]
    public async Task Target_outage_open_at_the_window_start_counts_from_the_window_start()
    {
        await AddTransition(_googleId, "Google", From.AddHours(-2), "Down");
        await AddTransition(_googleId, "Google", From.AddHours(1), "Responding");

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(1, google.OutageCount);
        Assert.Equal(TimeSpan.FromHours(1), google.LongestOutage);
    }

    [Fact]
    public async Task Target_down_for_the_whole_window_is_one_outage_the_length_of_the_window()
    {
        await AddTransition(_googleId, "Google", From.AddDays(-3), "Down");

        var report = await _builder.BuildAsync(From, To);

        var google = report.Targets.Single(t => t.Name == "Google");
        Assert.Equal(1, google.OutageCount);
        Assert.Equal(To - From, google.LongestOutage);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~Target_outage_open_at|FullyQualifiedName~Target_down_for_the_whole_window"`
Expected: both FAIL with OutageCount 0.

- [ ] **Step 3: Implement the report side**

`IspReportBuilder.cs`, replace lines 192-197 (the per-target loop body up to `ComputeIncidents`) with:

```csharp
        foreach (var target in targets)
        {
            var all = transitionsBySubject.GetValueOrDefault(target.Id) ?? [];
            var start = effectiveFrom[target.Id];
            // The state the target was in when the (clipped) window opened, so
            // an outage that began before it is counted from the window start —
            // the same rule BuildEpisodesAsync applies to the link verdict.
            var openingState = all.LastOrDefault(t => t.TimestampUtc <= start)?.ToState;
            var effective = all.Where(t => t.TimestampUtc > start).ToList();
            var stats = ComputeIncidents(effective, start, toUtc, openingState);
```

Change the `ComputeIncidents` signature (`:222`) and seed `open` from the opening state:

```csharp
    /// <summary>C# twin of the History page's split incident semantics. An
    /// outage or unreachable window already open at rangeStart counts from
    /// rangeStart (owner decision 2026-09-18: one rule everywhere).</summary>
    private static IncidentTotals ComputeIncidents(IReadOnlyList<TransitionRow> orderedRows,
        DateTime rangeStartUtc, DateTime rangeEndUtc, string? openingState)
    {
        int outageCount = 0;
        var longestOutage = TimeSpan.Zero;
        var unreachable = TimeSpan.Zero;
        (string Kind, DateTime Start)? open = openingState switch
        {
            "Down" => ("outage", rangeStartUtc),
            "Unreachable" => ("unreach", rangeStartUtc),
            _ => null,
        };
        if (open is { Kind: "outage" })
            outageCount++;
```

(the `Close` local and the loop are unchanged).

- [ ] **Step 4: Run the persistence suite**

Run: `dotnet test Tests/ConPersistence.Tests`
Expected: all pass (`Target_rows_honor_history_reset_clipping` still passes: the reset row's `ToState` is "Starting", which seeds nothing).

- [ ] **Step 5: Write the failing page test**

In `Programs/ConWatch/ClientApp/src/lib/historyData.test.ts`, add to the `incidents (split semantics)` block:

```ts
  it("an outage already open at the range start counts from the range start", () => {
    const stats = incidents([
      t("2026-08-20T01:00:00Z", "Responding", "Down"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(1);
    expect(stats.longestOutageMs).toBe(60 * 60_000);
  });

  it("an unreachable window already open at the range start counts from the range start", () => {
    const stats = incidents([
      t("2026-08-20T00:30:00Z", "Starting", "Unreachable", "ParentRecovered"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(0);
    expect(stats.unreachableMs).toBe(30 * 60_000);
  });
```

and change every existing `incidents(list, endIso)` call in that file to `incidents(list, "2026-08-20T00:00:00Z", endIso)`.

- [ ] **Step 6: Run to verify it fails**

Run: `cd Programs/ConWatch/ClientApp && npm test`
Expected: the two new tests FAIL (TypeScript arity error or `outageCount` 0).

- [ ] **Step 7: Implement the page side**

`historyData.ts`, replace lines 30-34 (the signature and the `open` initializer) with:

```ts
/**
 * Range-start seeding: the state before the first transition is that
 * transition's `from` (as ribbonSegments assumes). A Down or Unreachable
 * window already open when the range starts counts from rangeStart.
 * Limitation: with no in-range transition the pre-window state is unknown
 * and nothing is counted — the API sends no pre-window row.
 */
export function incidents(transitions: TransitionEvent[], rangeStartIso: string,
  rangeEndIso: string): IncidentStats {
  let outageCount = 0;
  let longestOutageMs = 0;
  let unreachableMs = 0;
  let open: { kind: "outage" | "unreach"; start: number } | null = null;
  const openingState = transitions[0]?.from;
  if (openingState === "Down") {
    open = { kind: "outage", start: Date.parse(rangeStartIso) };
    outageCount++;
  } else if (openingState === "Unreachable") {
    open = { kind: "unreach", start: Date.parse(rangeStartIso) };
  }
```

`history.tsx:127`: `const stats = incidents(clipped, (resetAt ?? from.toISOString()), to.toISOString());` (after a reset inside the window, the clipped series starts at the reset).

- [ ] **Step 8: Run the client tests and build**

Run: `cd Programs/ConWatch/ClientApp && npm test && npm run build`
Expected: all pass; build clean (the built bundle under `wwwroot/js` is ignored by git).

- [ ] **Step 9: Commit**

```bash
git add Libraries/ConPersistence/Report/IspReportBuilder.cs Tests/ConPersistence.Tests/IspReportBuilderTests.cs Programs/ConWatch/ClientApp/src/lib/historyData.ts Programs/ConWatch/ClientApp/src/lib/historyData.test.ts Programs/ConWatch/ClientApp/src/history.tsx
git commit -m "fix(report,history): an outage open at the window start counts from the window start"
```

---

### Task 3.4: The DbContext restores DateTimeKind.Utc on read; exports carry the offset (H3, P-B3)

**Files:**
- Modify: `Libraries/ConPersistence/ConWatchDbContext.cs:18-70`
- Modify: `Programs/ConWatch/Api/HistoryEndpoints.cs:24, 62`, `Api/GlitchEndpoints.cs:25-26`, `Api/AdminEndpoints.cs:73-74`, `Mcp/ConWatchMcpTools.cs:98, 121-122`, `Pages/Report.cshtml:9`, `Libraries/ConPersistence/Export/CsvExport.cs:74`
- Test: `Tests/ConPersistence.Tests/DbContextTests.cs` (add one test)
- Test: `Tests/ConWatch.Tests/ExportApiTests.cs` (add one test)

**Interfaces:** no signature changes. Behavior change (documented): `/export/*?format=json` and `/export/snapshot` timestamps now end in `Z`.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ConPersistence.Tests/DbContextTests.cs`:

```csharp
    [Fact]
    public async Task DateTimes_read_back_with_utc_kind()
    {
        var stamp = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);
        using (var db = new ConWatchDbContext(_options))
        {
            db.Transitions.Add(new TransitionRow
            {
                TimestampUtc = stamp, SubjectId = Guid.NewGuid(), SubjectName = "Router",
                FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
            });
            await db.SaveChangesAsync();
        }
        using (var db = new ConWatchDbContext(_options))
        {
            var row = await db.Transitions.SingleAsync();
            Assert.Equal(DateTimeKind.Utc, row.TimestampUtc.Kind);
            Assert.Equal(stamp, row.TimestampUtc);
        }
    }
```

Add to `Tests/ConWatch.Tests/ExportApiTests.cs`:

```csharp
    [Fact]
    public async Task Json_export_timestamps_carry_the_utc_marker()
    {
        await SeedRows(1);
        var body = await (await _client.GetAsync("/api/v1/export/transitions?format=json")).Content.ReadAsStringAsync();
        Assert.Matches("\"timestampUtc\":\"[^\"]+Z\"", body);
        var snapshot = await (await _client.GetAsync("/api/v1/export/snapshot")).Content.ReadAsStringAsync();
        Assert.Matches("\"periodStartUtc\":\"[^\"]+Z\"", snapshot);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~DateTimes_read_back"` → FAIL (`Kind` is `Unspecified`).
Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Json_export_timestamps"` → FAIL (no `Z`).

- [ ] **Step 3: Implement**

`ConWatchDbContext.cs`: add `using Microsoft.EntityFrameworkCore.Storage.ValueConversion;` and, at the END of `OnModelCreating` (after the `SettingRow` block):

```csharp
        // Every DateTime column is UTC by construction (the *Utc suffix is the
        // rule), but SQLite has no kind and EF loads them Unspecified. Restore
        // the kind on read so JSON serializers emit the Z and no caller has to
        // SpecifyKind by hand. Storage format is unchanged: no migration.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
                property.SetValueConverter(utc);
```

Then drop the hand stamps (each becomes a plain read of a now-Utc value):
- `HistoryEndpoints.cs:24`: `Timestamp = new DateTimeOffset(r.TimestampUtc),`
- `HistoryEndpoints.cs:62`: `PeriodStart = new DateTimeOffset(r.PeriodStartUtc),`
- `GlitchEndpoints.cs:25-26`: `StartUtc = new DateTimeOffset(g.StartUtc), EndUtc = new DateTimeOffset(g.EndUtc),`
- `AdminEndpoints.cs:73-74`: `static DateTimeOffset? AsUtc(DateTime? dt) => dt is { } d ? new DateTimeOffset(d) : null;` and delete the "Stamp the kind" comment above it.
- `ConWatchMcpTools.cs:98`: `TimestampUtc = t.TimestampUtc,`; `:121-122`: `StartUtc = g.StartUtc, EndUtc = g.EndUtc,`
- `Pages/Report.cshtml:9`: `new DateTimeOffset(utc):O` in place of `new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)):O`
- `CsvExport.cs:74`: `utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture)`

- [ ] **Step 4: Confirm no migration is needed and run all suites**

Run: `dotnet tool restore` then `dotnet ef migrations has-pending-model-changes --project Libraries/ConPersistence --startup-project Programs/ConWatch` (the tool is pinned in `dotnet-tools.json`)
Expected: "No changes have been made to the model since the last migration."

Run: `dotnet test ConnectionWatcher.sln`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConPersistence Programs/ConWatch Tests
git commit -m "fix(persistence): DateTime columns read back as UTC; JSON export and snapshot timestamps carry the Z"
```

---

### Task 3.5: Only a unique-index violation reads as "duplicate"; the incremental-vacuum no-op goes (P4, P5)

**Files:**
- Modify: `Libraries/ConPersistence/ConfigService.cs:283-289`
- Create: `Libraries/ConPersistence/SqliteErrors.cs`
- Modify: `Libraries/ConPersistence/Services/RetentionService.cs:44-49`
- Test: `Tests/ConPersistence.Tests/SqliteErrorsTests.cs` (new)

**Interfaces:**
- Produces: `SqliteErrors.IsUniqueViolation(DbUpdateException ex)` (static bool).

The review's second half of P4 (one read snapshot for `LoadWatcherOptionsAsync`) is **not** done here: Microsoft.Data.Sqlite's default transaction is `BEGIN IMMEDIATE`, which would make every config read take the write lock; a deferred read transaction needs the raw `SqliteConnection.BeginTransaction(deferred: true)` threaded through EF. Plan B, with the config-folder move.

- [ ] **Step 1: Write the failing test**

Create `Tests/ConPersistence.Tests/SqliteErrorsTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class SqliteErrorsTests
{
    [Fact]
    public void Unique_constraint_is_a_uniqueness_violation()
    {
        var inner = new SqliteException("UNIQUE constraint failed: Targets.Name", 19, 2067);
        Assert.True(SqliteErrors.IsUniqueViolation(new DbUpdateException("save failed", inner)));
    }

    [Fact]
    public void Other_constraints_and_other_causes_are_not()
    {
        var foreignKey = new SqliteException("FOREIGN KEY constraint failed", 19, 787);
        Assert.False(SqliteErrors.IsUniqueViolation(new DbUpdateException("save failed", foreignKey)));
        Assert.False(SqliteErrors.IsUniqueViolation(new DbUpdateException("save failed", new IOException("disk full"))));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~SqliteErrorsTests"`
Expected: build FAILS ("The name 'SqliteErrors' does not exist").

- [ ] **Step 3: Implement**

Create `Libraries/ConPersistence/SqliteErrors.cs`:

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence;

/// <summary>Classifies SQLite failures that EF surfaces as DbUpdateException.</summary>
public static class SqliteErrors
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE (extended code 2067).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
```

`ConfigService.cs:283-289`:

```csharp
        catch (DbUpdateException ex) when (SqliteErrors.IsUniqueViolation(ex))
        {
            _log.LogDebug(ex, "Config mutation rejected by a uniqueness constraint");
            return ConfigResult.Fail(ConfigErrorKind.Validation,
                "Change violates a uniqueness constraint (duplicate name or address).");
        }
```

(Any other `DbUpdateException` now propagates and the API answers 500, which is the truthful answer for a disk or schema problem.)

`RetentionService.cs:44-49`: replace with

```csharp
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
                await pass.RunAsync(db, timeProvider.GetUtcNow().UtcDateTime,
                    await EffectiveOptionsAsync().ConfigureAwait(false), stoppingToken).ConfigureAwait(false);
```

(`RetentionPass.RunAsync` keeps returning the converted count: the retention tests assert on it and the log lines use it. Space reclamation is the Data page's VACUUM, owner decision 2026-09-18.)

- [ ] **Step 4: Run the persistence suite**

Run: `dotnet test Tests/ConPersistence.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConPersistence Tests/ConPersistence.Tests/SqliteErrorsTests.cs
git commit -m "fix(persistence): only SQLITE_CONSTRAINT_UNIQUE reads as a duplicate; drop the no-op incremental_vacuum"
```

---
## Phase 4: Host

### Task 4.1: One `IEngineObserver` with `Detach()`; a disabled node detaches everything and tells open dashboards (S1, H2)

**Files:**
- Create: `Core/ConEngine/IEngineObserver.cs`
- Modify: `Libraries/ConPersistence/Services/ChannelRecorder.cs` (implement), `TransitionRecorder.cs`, `GlitchRecorder.cs`, `RollupCollector.cs`
- Modify: `Programs/ConWatch/StatusBroadcaster.cs`, `Programs/ConWatch/Metrics/MetricsCounters.cs`, `Libraries/ConNotify/MqttNotifier.cs`, `Libraries/ConNotify/WebhookNotifier.cs`
- Modify: `Programs/ConWatch/WatcherHostService.cs:17-36, 86-107, 113-157, 287-319`
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs:154-166`
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs:44`, `Programs/ConWatch/ClientApp/src/lib/types.ts:30-34`, `src/lib/statusStore.ts`, `src/dashboard.tsx:86`
- Test: `Tests/ConWatch.Tests/SignalRTests.cs` (add one test)

**Interfaces:**
- Produces (in `ConEngine`):

```csharp
public interface IEngineObserver
{
    /// <summary>Subscribe to a (re)started engine, replacing any prior subscription.</summary>
    void Attach(WatcherEngine engine);
    /// <summary>Drop the subscription and every reference to the engine.</summary>
    void Detach();
}
```

- `ChannelRecorder<TEvent>` gains `protected abstract IObservable<TEvent> SourceOf(WatcherEngine engine);`, `public void Attach(WatcherEngine engine)`, `public void Detach()`; the existing `Attach(IObservable<TEvent>)` stays public for the unit tests.
- `WatcherHostService(..., IEnumerable<IEngineObserver> observers)` replaces the four notifier/broadcaster/metrics parameters; the three recorder parameters stay for the archive-mode flush.
- Snapshot JSON gains `enabled: true`; the disabled snapshot is `{ enabled: false, verdict: null, targets: [], pools: [] }` (the `/status` archive shape).

- [ ] **Step 1: Write the failing test**

Add to `Tests/ConWatch.Tests/SignalRTests.cs`:

```csharp
    [Fact]
    public async Task Disabling_the_engine_pushes_one_disabled_snapshot_and_then_no_live_ones()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(() =>
        {
            lock (_snapshots)
                return Task.FromResult(_snapshots.Any(s => s.Snapshot.GetProperty("targets").GetArrayLength() > 0));
        });

        var off = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);

        await ConfigApiTests.Eventually(() =>
        {
            lock (_snapshots)
                return Task.FromResult(_snapshots.Any(s =>
                    s.Snapshot.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False));
        });

        // After the disabled snapshot nothing live-looking may follow.
        DateTime disabledAt;
        lock (_snapshots)
            disabledAt = _snapshots.First(s =>
                s.Snapshot.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False).At;
        await Task.Delay(2500); // more than two broadcaster cadences
        lock (_snapshots)
        {
            var after = _snapshots.Where(s => s.At > disabledAt).ToList();
            Assert.DoesNotContain(after, s => s.Snapshot.GetProperty("targets").GetArrayLength() > 0);
        }
    }
```

(`SignalRTests` needs `using System.Net.Http.Json;` for `PutAsJsonAsync`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Disabling_the_engine_pushes"`
Expected: FAIL at the second `Eventually`: no snapshot carries `enabled: false`; live snapshots from the dead engine keep arriving.

- [ ] **Step 3: The interface and the observers**

Create `Core/ConEngine/IEngineObserver.cs` with the interface from the Interfaces block above (namespace `ConEngine`).

`ChannelRecorder.cs`: add `using ConEngine;`, make the class `: BackgroundService, IEngineObserver`, and add:

```csharp
    /// <summary>Which of the engine's streams this recorder persists.</summary>
    protected abstract IObservable<TEvent> SourceOf(WatcherEngine engine);

    public void Attach(WatcherEngine engine) => Attach(SourceOf(engine));

    public void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
```

`TransitionRecorder.cs`: `protected override IObservable<TargetTransition> SourceOf(WatcherEngine engine) => engine.TransitionSource;` (add `using ConEngine;`).
`GlitchRecorder.cs`: `protected override IObservable<GlitchEvent> SourceOf(WatcherEngine engine) => engine.GlitchSource;`.

`RollupCollector.cs`: `: BackgroundService, IEngineObserver` and

```csharp
    public void Attach(WatcherEngine engine) => Attach(engine.PingSampleSource);

    public void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
```

`MetricsCounters.cs`: `: IEngineObserver`; `Detach()` disposes and clears `_subscriptions` under `_lock` (totals are kept: they are process-lifetime counters).

`WebhookNotifier.cs`: `: BackgroundService, IEngineObserver`; `Detach()` disposes `_subscription` and sets it null.

`MqttNotifier.cs`: `: BackgroundService, IEngineObserver`;

```csharp
    public void Detach()
    {
        lock (_lock)
        {
            _subscription?.Dispose();
            _subscription = null;
            _engine = null;
        }
        // The session itself ends on the Engine.Enabled reconfigure poke and
        // publishes availability=offline on the way out (Task 1.1).
    }
```

`StatusBroadcaster.cs`: `: BackgroundService, IEngineObserver`; add the disabled snapshot:

```csharp
    /// <summary>The archive-mode snapshot: the same shape /status answers when disabled.</summary>
    public static readonly object DisabledSnapshot = new
    {
        Enabled = false,
        Verdict = (object?)null,
        Targets = Array.Empty<object>(),
        Pools = Array.Empty<object>(),
    };

    public void Detach()
    {
        lock (_lock)
        {
            _subscription?.Dispose();
            _subscription = null;
            _engine = null;
        }
        // Open tabs were rendered live; one final push flips them to archive mode.
        _ = hub.Clients.All.SendAsync("statusChanged", DisabledSnapshot);
    }
```

and in `SendSnapshotAsync` the existing `if (snapshot is null) return;` already covers the detached case (`Build(null)` returns null).

`StatusSnapshotBuilder.cs:44`: `return new { Enabled = true, Verdict = verdict, Targets = targets, Pools = pools };`

- [ ] **Step 4: The host service and registrations**

`WatcherHostService.cs` constructor: delete the parameters `StatusBroadcaster broadcaster`, `Metrics.MetricsCounters metricsCounters`, `ConNotify.MqttNotifier mqttNotifier`, `ConNotify.WebhookNotifier webhookNotifier` and add `IEnumerable<IEngineObserver> observers` (add `using ConEngine;` if missing). Keep `recorder`, `glitchRecorder`, `collector` (archive flush).

Add a helper:

```csharp
    private void DetachObservers()
    {
        foreach (var observer in observers)
            observer.Detach();
    }
```

In `StartEngineAsync` (`:115-124`), the archive branch calls `DetachObservers();` before the flushes. Replace the attach block (`:146-152`) with:

```csharp
        foreach (var observer in observers)
            observer.Attach(engine);
```

In `StopAsync` (`:97-101`) and `RebuildAsync` (`:299-308`), call `DetachObservers();` right after `await engine.StopAsync();`.

`ConWatchServices.cs:154-166`: register the observers in attach order, next to the hosted services (same rule, one list):

```csharp
    public static IServiceCollection AddConWatchHostedServices(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<TransitionRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<GlitchRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<RollupCollector>());
        services.AddHostedService<RetentionService>();
        services.AddHostedService(sp => sp.GetRequiredService<ConNotify.MqttNotifier>());
        services.AddHostedService(sp => sp.GetRequiredService<ConNotify.WebhookNotifier>());
        services.AddHostedService<ConNotify.HeartbeatNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<StatusBroadcaster>());
        services.AddHostedService(sp => sp.GetRequiredService<WatcherHostService>());

        // Engine observers attach in THIS order on every engine start and detach
        // on stop/disable; recorders first so nothing is emitted before they listen.
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<TransitionRecorder>());
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<GlitchRecorder>());
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<RollupCollector>());
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<StatusBroadcaster>());
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<Metrics.MetricsCounters>());
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<ConNotify.MqttNotifier>());
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<ConNotify.WebhookNotifier>());
        return services;
    }
```

Update the summary comment at `ConWatchServices.cs:147-152`: the engine stops before the consumers because its token is `ApplicationStopping` (which fires before any `StopAsync`); registration order is the second guarantee.

- [ ] **Step 5: The client flips to archive mode on the disabled snapshot**

`src/lib/types.ts:30-34`: add `enabled?: boolean;` to `Snapshot`.

`src/lib/statusStore.ts`: add `disabled: boolean;` to `StoreState` (initial `false`), and in `applySnapshot`: `state.disabled = snap.enabled === false;` before `notify()`.

`src/dashboard.tsx:86`: `if (initial.engineDisabled || state.disabled) {` in place of `if (initial.engineDisabled) {`.

- [ ] **Step 6: Run everything**

Run: `dotnet test ConnectionWatcher.sln` and `cd Programs/ConWatch/ClientApp && npm test && npm run build`
Expected: all pass. `RecorderCollectorTests` still compile against `Attach(IObservable<T>)`.

- [ ] **Step 7: Commit**

```bash
git add Core/ConEngine/IEngineObserver.cs Libraries Programs Tests/ConWatch.Tests/SignalRTests.cs
git commit -m "refactor(host): one IEngineObserver with Detach; a disabled node detaches every observer and pushes a disabled snapshot"
```

---

### Task 4.2: A direct rebuild supersedes the debounced one and reports failure; the stop path disposes its debounce source (H4, H6)

**Files:**
- Modify: `Programs/ConWatch/WatcherHostService.cs:86-107, 287-319`
- Modify: `Programs/ConWatch/Api/AdminEndpoints.cs:27-38, 156-158`
- Modify: `Programs/ConWatch/Clone/CloneService.cs:70, 147`
- Modify: `docs/superpowers/reviews/2026-09-18-codebase-review.md` (H6 correction)
- Test: `Tests/ConWatch.Tests/MasterEnableTests.cs` (add one test)

**Interfaces:**
- Produces: `WatcherHostService.RebuildAsync()` returns `Task<bool>` (true when the engine state matches the settings afterwards); `WatcherHostService.RebuildCount` (`int`, test-visible, incremented per rebuild that ran).

- [ ] **Step 1: Write the failing test**

Add to `Tests/ConWatch.Tests/MasterEnableTests.cs`:

```csharp
    [Fact]
    public async Task M2M_engine_toggle_rebuilds_exactly_once()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(async () =>
            (await GetJson("/api/v1/health")).GetProperty("status").GetString() == "Healthy");
        await Task.Delay(500); // let the seeding rebuilds settle
        var before = host.RebuildCount;

        var off = await _client.PostAsJsonAsync("/api/v1/admin/engine", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        await Task.Delay(500); // a second, debounced rebuild would land in here

        Assert.Equal(before + 1, host.RebuildCount);
    }
```

(`MasterEnableTests` needs `using Microsoft.Extensions.DependencyInjection;`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~rebuilds_exactly_once"`
Expected: build FAILS (`RebuildCount` missing); after adding the counter alone it FAILS with `before + 2`.

- [ ] **Step 3: Implement**

`WatcherHostService.cs`: add a field and property near `_stopped`:

```csharp
    private int _rebuildCount;

    /// <summary>Rebuilds that actually ran (not the ones refused after stop). Test-visible.</summary>
    public int RebuildCount => Volatile.Read(ref _rebuildCount);
```

Replace `RebuildAsync` (`:287-319`) with:

```csharp
    /// <summary>
    /// Stops the engine and rebuilds it from current DB config. A direct call
    /// supersedes any debounced rebuild still pending for the same change, so
    /// an Engine.Enabled toggle rebuilds once, not twice. Returns false when
    /// the rebuild failed (the caller decides whether that is a 500).
    /// </summary>
    public async Task<bool> RebuildAsync()
    {
        lock (_debounceLock) { _debounceCts?.Cancel(); }
        await _rebuildLock.WaitAsync();
        try
        {
            if (_stopped)
            {
                _log.LogDebug("Rebuild requested after shutdown; ignored");
                return false;
            }
            Interlocked.Increment(ref _rebuildCount);
            _log.LogInformation("Rebuilding engine after config change");
            EngineSeed? seed = null;
            if (Engine is { } engine)
            {
                Engine = null;
                // Carry the outgoing engine's observed states into the new one
                // (fresh by definition) so a config edit doesn't re-measure the
                // world; the DB-seed path stays as fallback via StartEngineAsync.
                if (await WarmRestartEnabledAsync())
                    seed = BuildSeedFromEngine(engine);
                await engine.StopAsync();
                DetachObservers();
            }
            await StartEngineAsync(seed);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Engine rebuild failed");
            return false;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }
```

In `StopAsync` (`:90`): `lock (_debounceLock) { _debounceCts?.Cancel(); _debounceCts?.Dispose(); _debounceCts = null; }`.

`AdminEndpoints.cs:36-37`:

```csharp
            if (!await host.RebuildAsync())
                return Results.Json(new { errors = new[] { "The engine could not be rebuilt; see the log." } },
                    statusCode: 500);
            return Results.NoContent();
```

`AdminEndpoints.cs:157` (factory reset): same two-line shape before its `return Results.NoContent();`.

`CloneService.cs:70`: `if (!await host.RebuildAsync()) return CloneOutcome.Fail(500, "rebuild-failed", "The clone was imported but the archive could not be prepared; see this node's log.");`
`CloneService.cs:147`: `if (!await host.RebuildAsync()) return CloneOutcome.Fail(500, "rebuild-failed", "Data was final-synced but the engine could not start; see this node's log. The source is disabled.");`

`Tests/ConWatch.Tests/HostLifecycleTests.cs:24`: `await host.RebuildAsync();` still compiles (result unused).

Review correction: in `docs/superpowers/reviews/2026-09-18-codebase-review.md`, section H6, replace the "Winning debounce CTS never disposed" bullet's first sentence with: "**Debounce CTS at stop.** Re-checked 2026-09-18 while planning: `OnConfigChanged:326-327` disposes the previous source on every change, winner or not, so at most one is outstanding; only `StopAsync:90` leaves one behind. Fixed there in the follow-up plan's Task 4.2."

- [ ] **Step 4: Run the web suite**

Run: `dotnet test Tests/ConWatch.Tests`
Expected: all pass (`CloneTests` still get 200/OK on the happy paths).

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch Tests/ConWatch.Tests docs/superpowers/reviews/2026-09-18-codebase-review.md
git commit -m "fix(host): a direct rebuild supersedes the debounced one and reports failure as 500; stop disposes the debounce source"
```

---

### Task 4.3: Shutdown budget: 30 s container grace, 25 s host ceiling, 2 + 2 s engine grace, bounded MQTT goodbye (H5)

**Files:**
- Modify: `deploy/docker/compose.yaml` (service block)
- Modify: `Programs/ConWatch/Program.cs:16-28`
- Modify: `Core/ConEngine/WatcherEngine.cs:354`
- Modify: `Libraries/ConNotify/MqttNotifier.cs:128-139`
- Modify: `README.md` (Docker paragraph, ~line 322)
- Test: `Tests/ConWatch.Tests/HostSmokeTests.cs` (add one test)

- [ ] **Step 1: Write the failing test**

Add to `Tests/ConWatch.Tests/HostSmokeTests.cs`:

```csharp
    [Fact]
    public void Host_shutdown_ceiling_fits_under_the_container_grace()
    {
        var options = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(25), options.ShutdownTimeout);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Host_shutdown_ceiling"`
Expected: FAIL (default is 30 s).

- [ ] **Step 3: Implement**

`Program.cs`, after `builder.Services.AddWindowsService(...)` (line 16):

```csharp
// Shutdown budget (see docs/superpowers/reviews/2026-09-18-codebase-review.md, H5):
// the engine stops in <= 2+2 s, the notifiers say goodbye in <= 2 s, and the
// recorders' final flush gets the rest. 25 s sits under the 30 s
// stop_grace_period in deploy/docker/compose.yaml and the SCM's default.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
```

`WatcherEngine.cs:354`: `await manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);` (a probe is at most one profile timeout; the pump has nothing left once the controllers are gone).

`MqttNotifier.cs:128-139` (the `finally` block):

```csharp
            if (client.IsConnected)
            {
                try
                {
                    // Bounded goodbye: a black-holed broker must not eat the
                    // shutdown budget the recorders need.
                    using var goodbye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await PublishAsync(client, _topics.Availability, "offline", retain: true, goodbye.Token);
                    await client.DisconnectAsync(new MqttClientDisconnectOptions(), goodbye.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogDebug(ex, "MQTT disconnect cleanup failed");
                }
            }
```

`deploy/docker/compose.yaml`, inside the `conwatch` service after `restart: unless-stopped`:

```yaml
    # The app writes its last minute of data on the way out; give it time.
    # Synology Container Manager reads this from the compose file (there is
    # no UI for it). Docker's default is 10 s.
    stop_grace_period: 30s
```

`README.md` Docker paragraph (line 322): append "The compose file sets `stop_grace_period: 30s` so the last minute of rollups is written on stop; if you run the image with `docker run`, add `--stop-timeout 30`."

- [ ] **Step 4: Run the web and engine suites**

Run: `dotnet test Tests/ConWatch.Tests` and `dotnet test Tests/ConEngine.Tests`
Expected: all pass (`PingManagerTests` passes its own grace explicitly).

- [ ] **Step 5: Commit**

```bash
git add deploy/docker/compose.yaml Programs/ConWatch/Program.cs Core/ConEngine/WatcherEngine.cs Libraries/ConNotify/MqttNotifier.cs README.md Tests/ConWatch.Tests/HostSmokeTests.cs
git commit -m "fix(host): shutdown budget - 30 s container grace, 25 s host ceiling, 2+2 s engine grace, bounded MQTT goodbye"
```

---

## Finishing

- [ ] Run the whole suite once more: `dotnet test ConnectionWatcher.sln` and `cd Programs/ConWatch/ClientApp && npm test`. Record the counts in the review's README row for 2026-09-18 ("Follow-up plan" column) and tick the review's section-5 steps this plan covered (1–5, 7–9, 11 in full; 6 and 10 in part).
- [ ] Merge to master with `git merge --no-ff review-2026-09-18` when Mark says so. Never push.

## Self-review notes

- Spec coverage (review section 5 → tasks): step 1 (H1+S5) → 1.1; step 2 (E2 + resolver slimming) → 2.1, 2.2; step 3 (E1+S10) → 2.4, 2.5; step 4 (P1, P7) → 3.1; step 5 (P2 + rollup N+1) → 3.2; step 6 (H2+S1) → 4.1; step 7 (H3) → 3.4; step 8 (P3) → 3.3; step 9 (H4, H5, H6) → 4.2, 4.3; step 10 (E3) → 2.6 (snapshot half; outbox half deferred); step 11 (E4, E5, E6, P4, P5, P6) → 2.7, 2.3, 2.7, 3.5, 3.5, 3.2. Steps 12–16 (S2, S8, S3, S4, S6, S7, S9, S11, section 3 dedupe) are plan B.
- Dropped after re-check: the H6 CTS "leak per rebuild" (bounded to one; Task 4.2 handles the stop path). Deferred with a stated reason: E3's publish-outside-lock outbox; P4's read-snapshot transaction.
- Type consistency: `PingController(IPingTransceiverFactory, ILogger, TimeProvider)` and `PingManager(IPingTransceiverFactory, CancellationToken, ILoggerFactory, TimeProvider)` (2.3) are what 2.4's `LastResultUtc` and 2.5's `EngineHealth` build on; `WatcherEngine.LastResultUtc/StartedUtc/StallAfter` (2.4) are read by 2.5 only; `VerdictCoordinator.Current` (2.6) is read by the builders and `MqttNotifier`; `ChannelRecorder<TEvent>(log, capacity, batchSize, retryDelay)` (3.1) is what 4.1's `Attach(WatcherEngine)`/`Detach()` extend; `RollupBucket.Absorb` (3.2) is used only by `RollupCollector.Coalesce`; `incidents(transitions, rangeStartIso, rangeEndIso)` (3.3) has one caller, `history.tsx`; `WatcherHostService.EngineEnabled` (2.5) and `RebuildAsync(): Task<bool>` (4.2) are used by `StatusEndpoints`, `MetricsEndpoint`, `AdminEndpoints`, `CloneService`.
- Placeholder scan: no TBD/TODO; every code step carries the code; the two "if the fixture has no client field" notes (2.2) name the exact pattern file and lines to copy.
