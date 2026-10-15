# DNS Watch — Plan 4: Consumers — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry the name-resolution watch to the three things that talk to a person when they are not looking at the dashboard: webhooks get the DNS blame on a verdict change, Home Assistant gets a "Name resolution" binary sensor plus the blame on the verdict sensor, and the tray says "name lookups are failing" instead of plain "DOWN".

**Architecture:** The verdict transition already travels everywhere (webhooks, MQTT, history, the live status stream); it gains the `DnsBlame` the verdict coordinator already computes, and the shared `TransitionDto` serialises it as a `dns` block only when present. MQTT gets one more entity built by the same pure `HaDiscovery`/`StateMapping` builders the others use, published on refresh, on verdict changes, and on resolver-target changes; its own availability topic makes it *unavailable* when the watch is off. The tray's pure `TrayStatus`/`TrayToasts` helpers learn the verdict name, so the tooltip and toasts can tell `DnsDown` from `LinkDown` without touching the untestable WinForms shell beyond a few lines.

**Tech Stack:** .NET 10; `ConNotify` (MQTTnet, webhooks), `ConInfrastructure.Tray`, `ConEngine` events; xUnit.

**Spec:** `docs/superpowers/specs/2026-10-10-dns-watch-design.md` §5.4 (tray, webhooks, MQTT), §7 "Notifiers", rollout step 4. Plans 1–3 are merged (master a051bff / 4ed4a7e).

## Global Constraints

- **Tray** (spec §5.4): `TrayStatus` maps `DnsDown` to `TrayState.Down` (already true); the tooltip's first line for it is **Internet: name lookups are failing**; a toast on entry titled **Name lookups are failing** with body **Your internet is up, but name lookups are failing — {blame}** where `{blame}` is the verdict detail without its `DnsDown: ` prefix; a toast on exit like `LinkDown`'s (**Internet healthy again**, or **Internet DOWN** when the link itself then fails). Notes never toast (D3); nothing toasts on the first poll or while notifications are off.
- **Webhooks** (spec §5.4): `DnsDown` passes the "verdict only" filter (it is a Verdict-kind transition, so this already holds and gets a test); the verdict transition payload gains **`dns`** = `{ "kind": "YourResolver|NoResolver", "message": "...", "resolvers": ["10.1.10.1"] }`, present only when the verdict carries a blame (omitted otherwise, never `null`); resolver *target* transitions are already filtered like Local ones by `WatcherEngine.IsSideSubject` (scope ≠ Verdict).
- **MQTT** (spec §5.4): `InternetPayload(DnsDown) == "OFF"` (already true). New **`dns` binary sensor**: discovery at `{prefix}/binary_sensor/{base}_dns/config`, `name` **Name resolution**, `unique_id` `{base}_dns`, `device_class` `connectivity`, state topic `{base}/dns/state`, attributes topic `{base}/dns/attributes`, availability = **both** the node's `{base}/availability` and its own `{base}/dns/availability` with `availability_mode: "all"`; state **ON** for `Ok`, `Degraded`, `Filtered` and `Unknown`, **OFF** for `Down`; its availability topic reads **online** while the watch is on and **offline** when `DnsSummary.Watched` is false (so a switched-off watch reads *unavailable*, never OFF). Attributes always carry the keys `state`, `blame`, `systemResolvers`, `upstreamResolvers` (blame `null` and empty arrays when absent — stable keys for attribute templates); resolver entries `{ "name", "address", "state" }`. The verdict sensor's attributes gain **`dnsBlame`** (same shape as the webhook `dns` block, `null` when none). Every publish is retained except transitions, as today.
- The `DnsBlame` record (`ConEngine.Topology`, `(DnsBlameKind Kind, string Message, IReadOnlyList<string> Resolvers)`) is the single source; its `Kind` serialises as the enum name string.
- Repo rules: tests wait for conditions in deadline loops, never sleep-then-assert; no personal endpoints or addresses in code or tests (use documentation ranges such as `10.1.10.1`, `192.0.2.x`, `1.1.1.1`); commit per task; never push; no attribution lines in commits; build and test output pristine apart from the known NU1903 advisory and the pre-existing CS8631/CA2022/CA1416 warnings in untouched test files.
- Test commands: `dotnet test Tests/ConNotify.Tests`, `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Tray"`, `dotnet test ConnectionWatcher.sln`.

## Review Focus

1. **A verdict transition other than `DnsDown`** must serialise with no `dns` property at all (not `"dns": null`), so existing webhook consumers see an unchanged payload. Task 1 tests it.
2. **The watch switched off** must make the `dns` sensor *unavailable*, not OFF: an automation keyed on OFF must not fire because the person turned the watch off. Task 2 tests the availability payload for `Watched == false`.
3. **Attribute keys are stable**: with no blame and no upstream resolvers the attributes JSON still carries `blame: null`, `upstreamResolvers: []`. Task 2 tests it.
4. **`DnsDown` → `LinkDown`** is Down → Down for the tray state, yet the tooltip must change and a toast must say **Internet DOWN**; the reverse must toast **Name lookups are failing**. Task 3 tests both directions.
5. **A status with no verdict** (no anchor pool) must parse with `Verdict == null` and never throw or toast a DNS line. Task 3 tests it.

## File Structure

**Modify**
- `Core/ConEngine/Events/TargetTransition.cs` — `TransitionReason.Dns`.
- `Core/ConEngine/Topology/VerdictCoordinator.cs` — set `Dns = dns` on the verdict transition.
- `Libraries/ConPersistence/Queries/TransitionDto.cs` — `TransitionDnsBlame`, `Dns` property, mapping.
- `Libraries/ConNotify/Builders.cs` — `StateMapping.DnsPayload`, `DnsAvailabilityPayload`; `MqttTopics` dns topics; `HaDiscovery.DnsConfig`, `DnsAttributes`, `VerdictAttributes`; `DnsResolverEntry`.
- `Libraries/ConNotify/MqttNotifier.cs` — publish the dns entity; verdict attributes via the builder.
- `Libraries/ConInfrastructure/Tray/TrayStatus.cs` — `TrayInfo.Verdict`, `NamesFailing`, `Describe(TrayInfo)`.
- `Libraries/ConInfrastructure/Tray/TrayToasts.cs` — `Internet(previous, next, monitorUrl)`.
- `Programs/ConWatchTray/Program.cs` — keep the last `TrayInfo`; use the pure toast.
- `README.md` — Notifications section: the dns sensor, the webhook `dns` block, the tray toast.
- Tests: `Tests/ConNotify.Tests/BuildersTests.cs`, `WebhookFilterTests.cs`; `Tests/ConEngine.Tests` (the existing DnsDown verdict test); `Tests/ConWatch.Tests/TrayStatusTests.cs`, `TrayToastsTests.cs`.

---

### Task 1: The verdict transition carries the blame; webhooks publish it as `dns`

**Files:**
- Modify: `Core/ConEngine/Events/TargetTransition.cs`, `Core/ConEngine/Topology/VerdictCoordinator.cs` (~line 276), `Libraries/ConPersistence/Queries/TransitionDto.cs`
- Test: `Tests/ConNotify.Tests/BuildersTests.cs` (`WebhookPayloadTests`), `Tests/ConNotify.Tests/WebhookFilterTests.cs`, `Tests/ConEngine.Tests/VerdictDnsDownTests.cs` (the class that asserts a `DnsDown` verdict)

**Interfaces:**
- Produces: `TransitionReason.Dns` — `public ConEngine.Topology.DnsBlame? Dns { get; init; }` (set only on `VerdictChanged` transitions whose snapshot carries a blame). `TransitionDto.Dns` — `public TransitionDnsBlame? Dns { get; init; }` with `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`; `public sealed record TransitionDnsBlame(string Kind, string Message, IReadOnlyList<string> Resolvers)` in the same file; `TransitionDto.From(TargetTransition)` maps it, `From(TransitionRow)` leaves it null (history rows do not store it). Task 2 reads `t.Reason.Dns` for the MQTT verdict attributes.

- [ ] **Step 1: Write the failing tests**

Append to `WebhookPayloadTests` in `BuildersTests.cs`:

```csharp
    [Fact]
    public void A_DnsDown_verdict_carries_the_blame_as_dns_and_other_verdicts_omit_it()
    {
        var blame = new ConEngine.Topology.DnsBlame(ConEngine.Topology.DnsBlameKind.YourResolver,
            "Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", ["10.1.10.1"]);
        var dnsDown = new TargetTransition
        {
            Kind = TransitionSubjectKind.Verdict, TargetId = Guid.NewGuid(), TargetName = "Internet",
            From = HostState.Responding, To = HostState.Down, Timestamp = DateTimeOffset.Parse("2026-10-15T12:00:00Z"),
            Reason = new TransitionReason
            {
                Trigger = TransitionTrigger.VerdictChanged, Verdict = "DnsDown",
                Detail = "DnsDown: Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", Dns = blame,
            },
        };
        var json = JsonSerializer.Deserialize<JsonElement>(WebhookPayload.ToJson(dnsDown));
        var dns = json.GetProperty("dns");
        Assert.Equal("YourResolver", dns.GetProperty("kind").GetString());
        Assert.Contains("10.1.10.1", dns.GetProperty("message").GetString());
        Assert.Equal(["10.1.10.1"], dns.GetProperty("resolvers").EnumerateArray().Select(e => e.GetString()).ToArray());

        var linkDown = dnsDown with { Reason = new TransitionReason { Trigger = TransitionTrigger.VerdictChanged, Verdict = "LinkDown", Detail = "LinkDown: Modem is Down" } };
        var other = JsonSerializer.Deserialize<JsonElement>(WebhookPayload.ToJson(linkDown));
        Assert.False(other.TryGetProperty("dns", out _)); // omitted, never null (Review Focus 1)
    }
```

Append to `WebhookFilterTests`:

```csharp
    [Fact]
    public void A_DnsDown_verdict_passes_verdict_only_and_a_resolver_target_flap_is_a_target_like_any_other()
    {
        var verdict = T(HostState.Responding, HostState.Down, TransitionSubjectKind.Verdict, TransitionTrigger.VerdictChanged) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.VerdictChanged, Verdict = "DnsDown" },
        };
        Assert.True(WebhookFilter.ShouldDeliver(verdict, verdictOnly: true, skipExplained: true));
        // The scope filter lives in WebhookNotifier.Attach (IsSideSubject); the pure filter only sees kinds.
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), verdictOnly: true, skipExplained: true));
    }
```

In the ConEngine test that asserts a `DnsDown` verdict, add an assertion on the emitted transition's reason: find where that test reads the verdict transition (its `Reason.Verdict == "DnsDown"` or the coordinator's `Current`), and assert `Assert.NotNull(transition.Reason.Dns); Assert.Equal(DnsBlameKind.YourResolver, transition.Reason.Dns!.Kind);` (or `NoResolver`, whichever that scenario produces — read the test's setup: an answering upstream means `YourResolver`). If the test only inspects `Current`, extend it to capture the transition through the same subscription other tests in the file use.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConNotify.Tests` and the ConEngine class you edited.
Expected: FAIL (compile: `Dns` not a member of `TransitionReason`).

- [ ] **Step 3: Implement**

`TargetTransition.cs`, in `TransitionReason` after `Verdict`:

```csharp
    /// <summary>The name-resolution blame when Verdict is DnsDown (spec 2026-10-10 §5.4):
    /// who isn't answering and whether a public resolver still does. Null for every other verdict.</summary>
    public ConEngine.Topology.DnsBlame? Dns { get; init; }
```

`VerdictCoordinator.cs` (~line 276): the transition's `Reason` gains `Dns = dns,` beside `Detail = detail`.

`TransitionDto.cs`:

```csharp
/// <summary>The DnsDown blame on a verdict transition, as webhooks and MQTT publish it.</summary>
public sealed record TransitionDnsBlame(string Kind, string Message, IReadOnlyList<string> Resolvers)
{
    public static TransitionDnsBlame? From(ConEngine.Topology.DnsBlame? b) =>
        b is null ? null : new(b.Kind.ToString(), b.Message, b.Resolvers);
}
```

and in `TransitionDto`, after `Scope`:

```csharp
    /// <summary>Present only on a DnsDown verdict transition (live messages); history rows do not keep it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TransitionDnsBlame? Dns { get; init; }
```

with `Dns = TransitionDnsBlame.From(t.Reason.Dns),` in `From(TargetTransition)`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConNotify.Tests`, the edited ConEngine class, and `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~Transition"`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/Events/TargetTransition.cs Core/ConEngine/Topology/VerdictCoordinator.cs Libraries/ConPersistence/Queries/TransitionDto.cs Tests/ConNotify.Tests/BuildersTests.cs Tests/ConNotify.Tests/WebhookFilterTests.cs Tests/ConEngine.Tests
git commit -m "feat(notify): a DnsDown verdict transition carries its blame; webhooks publish it as dns"
```

---

### Task 2: The MQTT "Name resolution" binary sensor and the verdict's `dnsBlame`

**Files:**
- Modify: `Libraries/ConNotify/Builders.cs`, `Libraries/ConNotify/MqttNotifier.cs`, `README.md` (Notifications section)
- Test: `Tests/ConNotify.Tests/BuildersTests.cs`

**Interfaces:**
- Consumes: `TransitionReason.Dns` (Task 1); `WatcherEngine.DnsWatch` (`ConEngine.Dns.DnsWatch(Enabled, System, Upstream)` of `TargetMonitor`), `WatcherEngine.DnsResults` (`DnsResultLog`), `ConEngine.Dns.DnsSummary.Compute(watch, log)` → `(bool Watched, DnsSummaryState State)`, `engine.Verdict?.Current.Dns` (`DnsBlame?`), `engine.Graph.Targets[id].Scope`.
- Produces (`ConNotify`):
  ```csharp
  public static string StateMapping.DnsPayload(DnsSummaryState state);        // "OFF" for Down, else "ON"
  public static string StateMapping.DnsAvailabilityPayload(bool watched);     // "online" / "offline"
  public string MqttTopics.DnsState / DnsAttributes / DnsAvailability / DnsDiscovery;
  public sealed record DnsResolverEntry(string Name, string Address, string State) { public static DnsResolverEntry From(TargetMonitor m); }
  public static string HaDiscovery.DnsConfig(MqttTopics topics);
  public static string HaDiscovery.DnsAttributes(DnsSummary summary, DnsBlame? blame, IReadOnlyList<DnsResolverEntry> system, IReadOnlyList<DnsResolverEntry> upstream);
  public static string HaDiscovery.VerdictAttributes(string? detail, string? @operator, DnsBlame? dnsBlame);
  ```

- [ ] **Step 1: Write the failing tests**

Append to `BuildersTests.cs` (in `StateMappingTests`, `MqttTopicsTests`, and `HaDiscoveryTests`, matching each class's style):

```csharp
    // StateMappingTests
    [Theory]
    [InlineData(DnsSummaryState.Ok, "ON")]
    [InlineData(DnsSummaryState.Degraded, "ON")]
    [InlineData(DnsSummaryState.Filtered, "ON")]
    [InlineData(DnsSummaryState.Unknown, "ON")]
    [InlineData(DnsSummaryState.Down, "OFF")]
    public void Dns_binary_is_off_only_when_every_resolver_is_down(DnsSummaryState state, string expected) =>
        Assert.Equal(expected, StateMapping.DnsPayload(state));

    [Fact]
    public void Dns_sensor_is_unavailable_not_off_when_the_watch_is_off() // Review Focus 2
    {
        Assert.Equal("offline", StateMapping.DnsAvailabilityPayload(watched: false));
        Assert.Equal("online", StateMapping.DnsAvailabilityPayload(watched: true));
    }

    // MqttTopicsTests
    [Fact]
    public void Dns_topics_hang_off_the_base()
    {
        Assert.Equal("conwatch/dns/state", _topics.DnsState);
        Assert.Equal("conwatch/dns/attributes", _topics.DnsAttributes);
        Assert.Equal("conwatch/dns/availability", _topics.DnsAvailability);
        Assert.Equal("homeassistant/binary_sensor/conwatch_dns/config", _topics.DnsDiscovery);
    }

    // HaDiscoveryTests
    [Fact]
    public void Dns_discovery_document_needs_both_availabilities()
    {
        var json = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.DnsConfig(Topics));
        Assert.Equal("Name resolution", json.GetProperty("name").GetString());
        Assert.Equal("conwatch_dns", json.GetProperty("unique_id").GetString());
        Assert.Equal("conwatch/dns/state", json.GetProperty("state_topic").GetString());
        Assert.Equal("conwatch/dns/attributes", json.GetProperty("json_attributes_topic").GetString());
        Assert.Equal("connectivity", json.GetProperty("device_class").GetString());
        Assert.Equal("all", json.GetProperty("availability_mode").GetString());
        Assert.Equal(["conwatch/availability", "conwatch/dns/availability"],
            json.GetProperty("availability").EnumerateArray().Select(a => a.GetProperty("topic").GetString()).ToArray());
        Assert.Equal("conwatch", json.GetProperty("device").GetProperty("identifiers")[0].GetString());
    }

    [Fact]
    public void Dns_attributes_carry_stable_keys_even_when_empty() // Review Focus 3
    {
        var empty = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.DnsAttributes(
            new DnsSummary(true, DnsSummaryState.Ok), null,
            [new DnsResolverEntry("Your resolver 10.1.10.1", "10.1.10.1", "Responding")], []));
        Assert.Equal("Ok", empty.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("blame").ValueKind);
        Assert.Equal(0, empty.GetProperty("upstreamResolvers").GetArrayLength());
        var sys = Assert.Single(empty.GetProperty("systemResolvers").EnumerateArray());
        Assert.Equal("10.1.10.1", sys.GetProperty("address").GetString());
        Assert.Equal("Responding", sys.GetProperty("state").GetString());

        var blame = new DnsBlame(DnsBlameKind.NoResolver, "No resolver is answering, though the internet is reachable.", ["10.1.10.1"]);
        var down = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.DnsAttributes(
            new DnsSummary(true, DnsSummaryState.Down), blame, [], [new DnsResolverEntry("1.1.1.1 directly", "1.1.1.1", "Down")]));
        Assert.Equal("NoResolver", down.GetProperty("blame").GetProperty("kind").GetString());
        Assert.Equal("1.1.1.1", down.GetProperty("upstreamResolvers")[0].GetProperty("address").GetString());
    }

    [Fact]
    public void Verdict_attributes_gain_the_dns_blame()
    {
        var none = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.VerdictAttributes("Healthy: 2 anchors responding", null, null));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("dnsBlame").ValueKind);
        Assert.Equal(JsonValueKind.Null, none.GetProperty("operator").ValueKind);
        var blame = new DnsBlame(DnsBlameKind.YourResolver, "Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", ["10.1.10.1"]);
        var some = JsonSerializer.Deserialize<JsonElement>(HaDiscovery.VerdictAttributes("DnsDown: …", null, blame));
        Assert.Equal("YourResolver", some.GetProperty("dnsBlame").GetProperty("kind").GetString());
        Assert.Equal("Comcast", JsonSerializer.Deserialize<JsonElement>(HaDiscovery.VerdictAttributes("d", "Comcast", null)).GetProperty("operator").GetString());
    }
```

Add `using ConEngine.Dns; using ConEngine.Topology;` at the top of the test file.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConNotify.Tests`
Expected: FAIL (compile).

- [ ] **Step 3: Implement the builders**

`Builders.cs` additions (keep the existing members; add `using ConEngine.Dns; using ConEngine.Topology;`):

```csharp
// in StateMapping
    /// <summary>The name-resolution binary (spec 2026-10-10 §5.4): OFF only when every
    /// resolver is down; Degraded/Filtered/Unknown still read ON. Not-watched is handled by
    /// availability, never by OFF — an automation must not fire because the watch was switched off.</summary>
    public static string DnsPayload(DnsSummaryState state) => state == DnsSummaryState.Down ? "OFF" : "ON";

    public static string DnsAvailabilityPayload(bool watched) => watched ? "online" : "offline";

// in MqttTopics
    public string DnsState => $"{Base}/dns/state";
    public string DnsAttributes => $"{Base}/dns/attributes";
    public string DnsAvailability => $"{Base}/dns/availability";
    public string DnsDiscovery => $"{Prefix}/binary_sensor/{Base}_dns/config";

// top level
/// <summary>A resolver as the dns sensor's attributes list it.</summary>
public sealed record DnsResolverEntry(string Name, string Address, string State)
{
    public static DnsResolverEntry From(TargetMonitor m) => new(m.Target.Name, m.Target.Address, m.CurrentState.ToString());
}

// in HaDiscovery
    /// <summary>Unavailable when either the node or the watch is off (availability_mode "all").</summary>
    public static string DnsConfig(MqttTopics topics) =>
        JsonSerializer.Serialize(new
        {
            name = "Name resolution",
            unique_id = $"{topics.Base}_dns",
            state_topic = topics.DnsState,
            json_attributes_topic = topics.DnsAttributes,
            device_class = "connectivity",
            availability = new[] { new { topic = topics.Availability }, new { topic = topics.DnsAvailability } },
            availability_mode = "all",
            device = Device(topics),
        }, Options);

    private static object? Blame(DnsBlame? b) => b is null ? null : new { kind = b.Kind.ToString(), message = b.Message, resolvers = b.Resolvers };

    /// <summary>Keys are always present (attribute templates need them): state, blame (null when none),
    /// systemResolvers, upstreamResolvers.</summary>
    public static string DnsAttributes(DnsSummary summary, DnsBlame? blame,
        IReadOnlyList<DnsResolverEntry> system, IReadOnlyList<DnsResolverEntry> upstream) =>
        JsonSerializer.Serialize(new
        {
            state = summary.State.ToString(),
            blame = Blame(blame),
            systemResolvers = system.Select(r => new { name = r.Name, address = r.Address, state = r.State }),
            upstreamResolvers = upstream.Select(r => new { name = r.Name, address = r.Address, state = r.State }),
        }, Options);

    public static string VerdictAttributes(string? detail, string? @operator, DnsBlame? dnsBlame) =>
        JsonSerializer.Serialize(new { detail, @operator, dnsBlame = Blame(dnsBlame) }, Options);
```

(`TargetMonitor` is `ConEngine.Tracking.TargetMonitor`, already imported. `Options` is `WriteIndented = false` with default naming — the anonymous-object property names are the wire names, as the existing builders do.)

- [ ] **Step 4: Implement the notifier**

`MqttNotifier.cs`:

- `RefreshAllAsync`: after the internet discovery publish, `await PublishAsync(client, _topics.DnsDiscovery, HaDiscovery.DnsConfig(_topics), retain: true, ct);`; replace the inline verdict-attributes serialisation with `HaDiscovery.VerdictAttributes(current?.Detail, current?.Operator, current?.Dns)`; then `await PublishDnsAsync(client, engine, ct);`.
- `PublishTransitionAsync`: in the `Verdict` case use `HaDiscovery.VerdictAttributes(t.Reason.Detail, engineForOperator?.Verdict?.CurrentOperator, t.Reason.Dns)` and, after the internet state, `if (engineForOperator is not null) await PublishDnsAsync(client, engineForOperator, ct);`. In the `Target` case, after publishing the target state: look the target up — `WatcherEngine? engine; lock (_lock) { engine = _engine; }` and if `engine is not null && IsResolver(engine, t.TargetId)` then `await PublishDnsAsync(client, engine, ct);` (a resolver's own change moves the summary; nothing else does between verdicts). `engine.Graph` throws `InvalidOperationException` once the engine has stopped, so the helper swallows exactly that:

```csharp
    private static bool IsResolver(WatcherEngine engine, Guid targetId)
    {
        try
        {
            return engine.Graph.Targets.TryGetValue(targetId, out var target) && target.Scope == TargetScope.Resolver;
        }
        catch (InvalidOperationException)
        {
            return false; // engine stopped between the transition and its publish
        }
    }
```
- New method:

```csharp
    /// <summary>The name-resolution entity: its own availability says whether the watch is on
    /// (spec 2026-10-10 §5.4); state and attributes come from the same summary the status block uses.</summary>
    private async Task PublishDnsAsync(IMqttClient client, WatcherEngine engine, CancellationToken ct)
    {
        if (_topics is null)
            return;
        var watch = engine.DnsWatch;
        var summary = DnsSummary.Compute(watch, engine.DnsResults);
        await PublishAsync(client, _topics.DnsAvailability, StateMapping.DnsAvailabilityPayload(summary.Watched), retain: true, ct);
        await PublishAsync(client, _topics.DnsState, StateMapping.DnsPayload(summary.State), retain: true, ct);
        await PublishAsync(client, _topics.DnsAttributes, HaDiscovery.DnsAttributes(summary, engine.Verdict?.Current.Dns,
            watch.System.Select(DnsResolverEntry.From).ToList(), watch.Upstream.Select(DnsResolverEntry.From).ToList()), retain: true, ct);
    }
```

Add `using ConCore.Targets; using ConEngine.Dns;`. If `engine.DnsResults` or `engine.Graph` has a different accessor name, read `Core/ConEngine/WatcherEngine.cs` (`StatusSnapshotBuilder.Build` at `Programs/ConWatch/StatusSnapshotBuilder.cs:94-100` uses the right ones) and match it.

- [ ] **Step 5: README**

In "Notifications", the MQTT bullet list (after the internet binary sensor bullet):

```markdown
- a **name-resolution binary sensor** (ON while your resolvers answer, OFF when
  none does; *unavailable* while the watch is off; attributes carry the resolver
  lists and the blame),
```

and in the verdict sensor bullet, change "with the detail text as an attribute" to "with the detail text and, on `DnsDown`, the blame as attributes". In the **Webhooks** paragraph, after "verdict/evidence fields)", add: "a `DnsDown` verdict change also carries a `dns` block naming the resolver that stopped answering."

- [ ] **Step 6: Run the tests**

Run: `dotnet test Tests/ConNotify.Tests` and `dotnet build Libraries/ConNotify`
Expected: PASS, no new warnings.

- [ ] **Step 7: Commit**

```bash
git add Libraries/ConNotify/Builders.cs Libraries/ConNotify/MqttNotifier.cs Tests/ConNotify.Tests/BuildersTests.cs README.md
git commit -m "feat(mqtt): a Name resolution binary sensor with its own availability; the verdict sensor carries dnsBlame"
```

---

### Task 3: The tray tells name lookups from a dead link

**Files:**
- Modify: `Libraries/ConInfrastructure/Tray/TrayStatus.cs`, `Libraries/ConInfrastructure/Tray/TrayToasts.cs`, `Programs/ConWatchTray/Program.cs` (~lines 36-58 fields, 131-191 refresh + `MaybeToast`), `README.md` (only if a tray paragraph lists what toasts — `grep -n -i "tray" README.md`)
- Test: `Tests/ConWatch.Tests/TrayStatusTests.cs`, `Tests/ConWatch.Tests/TrayToastsTests.cs`

**Interfaces:**
- Produces: `TrayInfo` gains `public string? Verdict { get; init; }` (the verdict's `state` string, null without a verdict) and `public bool NamesFailing => Verdict == "DnsDown";`. `TrayStatus.FromStatusJson` sets `Verdict`. `TrayStatus.Describe(TrayInfo info)` overload: `Down && NamesFailing` → **Internet: name lookups are failing**, else `Describe(info.State)`; `Tooltip` uses it. `TrayToasts.Internet(TrayInfo previous, TrayInfo next, string monitorUrl)` → `TrayToast?`, pure: the state/verdict-change toast (callers still gate on first poll and the notifications setting). `Program.cs` keeps `private TrayInfo _last = new(TrayState.Unreachable, null, []);` in place of `_state`.

- [ ] **Step 1: Write the failing tests**

Append to `TrayStatusTests`:

```csharp
    [Fact]
    public void The_verdict_name_is_kept_and_DnsDown_reads_as_name_lookups_failing_in_the_tooltip()
    {
        var info = TrayStatus.FromStatusJson(Status("Responding", verdict: "DnsDown"));
        Assert.Equal("DnsDown", info.Verdict);
        Assert.True(info.NamesFailing);
        Assert.Equal(TrayState.Down, info.State);
        Assert.StartsWith("Internet: name lookups are failing\n", TrayStatus.Tooltip(info));

        var link = TrayStatus.FromStatusJson(Status("Responding", verdict: "LinkDown"));
        Assert.False(link.NamesFailing);
        Assert.StartsWith("Internet: DOWN\n", TrayStatus.Tooltip(link));
    }

    [Fact]
    public void A_status_without_a_verdict_has_no_verdict_name() // Review Focus 5
    {
        var info = TrayStatus.FromStatusJson("""{"enabled":true,"verdict":null,"targets":[{"id":"a","name":"Router","state":"Down","scope":"Verdict","critical":false}],"pools":[]}""");
        Assert.Null(info.Verdict);
        Assert.False(info.NamesFailing);
        Assert.Equal(TrayState.Down, info.State);
        Assert.StartsWith("Internet: DOWN", TrayStatus.Tooltip(info));
    }
```

Append to `TrayToastsTests`:

```csharp
    private static TrayInfo Info(TrayState state, string? verdict = null, string? detail = null) =>
        new(state, detail, []) { Verdict = verdict };

    [Fact]
    public void Entering_DnsDown_says_name_lookups_are_failing_with_the_blame()
    {
        var toast = TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"),
            Info(TrayState.Down, "DnsDown", "DnsDown: Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly."), "http://localhost:8080")!;
        Assert.Equal("Name lookups are failing", toast.Title);
        Assert.Equal("Your internet is up, but name lookups are failing — Your resolver 10.1.10.1 isn't answering, but 1.1.1.1 answers directly.", toast.Body);
        Assert.Equal(ToastLevel.Error, toast.Level);
    }

    [Fact]
    public void DnsDown_and_LinkDown_toast_each_other_even_though_both_are_Down() // Review Focus 4
    {
        var dns = Info(TrayState.Down, "DnsDown", "DnsDown: No resolver is answering, though the internet is reachable.");
        var link = Info(TrayState.Down, "LinkDown", "LinkDown: Modem is Down");
        Assert.Equal("Internet DOWN", TrayToasts.Internet(dns, link, "u")!.Title);
        Assert.Equal("LinkDown: Modem is Down", TrayToasts.Internet(dns, link, "u")!.Body);
        Assert.Equal("Name lookups are failing", TrayToasts.Internet(link, dns, "u")!.Title);
        Assert.Null(TrayToasts.Internet(dns, dns with { Detail = "DnsDown: changed wording" }, "u")); // same verdict: no toast
    }

    [Fact]
    public void Leaving_DnsDown_for_healthy_toasts_the_recovery()
    {
        var toast = TrayToasts.Internet(Info(TrayState.Down, "DnsDown", "d"), Info(TrayState.Healthy, "Healthy", "Healthy: 2 anchors responding"), "u")!;
        Assert.Equal("Internet healthy again", toast.Title);
        Assert.Equal("Name lookups are working again.", toast.Body);
        Assert.Equal(ToastLevel.Info, toast.Level);
    }

    [Fact]
    public void The_old_toasts_are_unchanged_and_measuring_never_toasts()
    {
        Assert.Equal(("Internet DOWN", ToastLevel.Error), Pick(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Down, "LinkDown", "LinkDown: Modem is Down"), "u")));
        Assert.Equal(("Internet degraded", ToastLevel.Warning), Pick(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Degraded, "LinkDegraded", "d"), "u")));
        Assert.Equal(("Internet healthy again", ToastLevel.Info), Pick(TrayToasts.Internet(Info(TrayState.Down, "LinkDown"), Info(TrayState.Healthy, "Healthy", "h"), "u")));
        var unreachable = TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Unreachable), "http://nas:8080")!;
        Assert.Equal("ConnectionWatcher unreachable", unreachable.Title);
        Assert.Equal("The monitor at http://nas:8080 is not answering.", unreachable.Body);
        Assert.Null(TrayToasts.Internet(Info(TrayState.Measuring), Info(TrayState.Healthy, "Healthy"), "u"));
        Assert.Null(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Measuring), "u"));
        Assert.Null(TrayToasts.Internet(Info(TrayState.Healthy, "Healthy"), Info(TrayState.Healthy, "SiteIncident", "s"), "u")); // still healthy: no toast
    }

    private static (string, ToastLevel) Pick(TrayToast? t) => (t!.Title, t.Level);
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Tray"`
Expected: FAIL (compile: `Verdict`, `Internet`).

- [ ] **Step 3: Implement the pure parts**

`TrayStatus.cs`:

```csharp
public record TrayInfo(TrayState State, string? Detail, IReadOnlyList<string> Notes)
{
    public IReadOnlyList<TrayCritical> Critical { get; init; } = [];
    /// <summary>The verdict's state name ("Healthy", "DnsDown", …), null without a verdict.</summary>
    public string? Verdict { get; init; }
    /// <summary>DnsDown: the link is up, the resolvers are not (spec 2026-10-10 §5.4).</summary>
    public bool NamesFailing => Verdict == "DnsDown";
}
```

In `FromStatusJson`, the verdict branch: `var verdictName = verdict.GetProperty("state").GetString();` then `return new TrayInfo(state, detail, notes) { Critical = critical, Verdict = verdictName };` (switch on `verdictName`). Add:

```csharp
    public static string Describe(TrayInfo info) =>
        info.State == TrayState.Down && info.NamesFailing ? "Internet: name lookups are failing" : Describe(info.State);
```

and make `Tooltip` start from `Describe(info)`.

`TrayToasts.cs`:

```csharp
    /// <summary>
    /// The internet toast for a poll (spec 2026-10-09 tray; 2026-10-10 §5.4): on a change of
    /// state, or of verdict between the two Down kinds. Never while measuring. The caller
    /// still gates on the first poll and the notifications setting.
    /// </summary>
    public static TrayToast? Internet(TrayInfo previous, TrayInfo next, string monitorUrl)
    {
        if (next.State == TrayState.Measuring || previous.State == TrayState.Measuring)
            return null;
        if (next.State == previous.State && next.NamesFailing == previous.NamesFailing)
            return null;

        if (next.State == TrayState.Down && next.NamesFailing)
        {
            var blame = (next.Detail ?? "").StartsWith("DnsDown: ", StringComparison.Ordinal)
                ? next.Detail![9..] : next.Detail is { Length: > 0 } d ? d : "your resolver isn't answering";
            return new TrayToast("Name lookups are failing", $"Your internet is up, but name lookups are failing — {blame}", ToastLevel.Error);
        }
        (string? title, ToastLevel level) = next.State switch
        {
            TrayState.Down => ("Internet DOWN", ToastLevel.Error),
            TrayState.Degraded => ("Internet degraded", ToastLevel.Warning),
            TrayState.Healthy => ("Internet healthy again", ToastLevel.Info),
            TrayState.Unreachable => ("ConnectionWatcher unreachable", ToastLevel.Warning),
            _ => (null, ToastLevel.Info),
        };
        if (title is null)
            return null;
        var body = next.State == TrayState.Unreachable
            ? $"The monitor at {monitorUrl} is not answering."
            : next.State == TrayState.Healthy && previous.NamesFailing ? "Name lookups are working again."
            : next.Detail is { Length: > 0 } ? next.Detail : TrayStatus.Describe(next);
        return new TrayToast(title, body, level);
    }
```

- [ ] **Step 4: Wire the tray shell**

`Program.cs`: replace `private TrayState _state = TrayState.Unreachable;` with `private TrayInfo _last = new(TrayState.Unreachable, null, []);`. In `RefreshAsync`: `bool stateChanged = next.State != _last.State || next.NamesFailing != _last.NamesFailing;` keep the notes/critical checks; inside the `if`, `var previous = _last; _last = next;` (drop `_state = next.State`), and `internetToast = MaybeToast(previous, next);`. Replace `MaybeToast`'s body with:

```csharp
    private TrayToast? MaybeToast(TrayInfo previous, TrayInfo next)
    {
        if (_firstPoll || !_settings.NotificationsEnabled)
            return null;
        return TrayToasts.Internet(previous, next, _settings.EffectiveUrl);
    }
```

Everywhere else `_state` was read, use `_last.State`. `IconFor(next.State, …)` is unchanged: DnsDown stays the red icon. Build the tray project: `dotnet build Programs/ConWatchTray` (Windows only; it is in this solution).

- [ ] **Step 5: README**

Run `grep -n -i "tray" README.md`. If a paragraph lists what the tray toasts, add one clause: "…and **Name lookups are failing** when your resolvers stop answering while the link is up." Otherwise leave the README alone and say so in the report.

- [ ] **Step 6: Run everything**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~Tray"`, then `dotnet test ConnectionWatcher.sln` (allow 10 minutes; the known flake `EndToEndTests.Verdict_walks_the_full_scenario_and_history_records_it` may fail under load — re-run it alone if it is the only failure and report both results).
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Libraries/ConInfrastructure/Tray/TrayStatus.cs Libraries/ConInfrastructure/Tray/TrayToasts.cs Programs/ConWatchTray/Program.cs Tests/ConWatch.Tests/TrayStatusTests.cs Tests/ConWatch.Tests/TrayToastsTests.cs README.md
git commit -m "feat(tray): name lookups failing is its own tooltip and toast, told apart from a dead link"
```

---

## Self-review notes

- **Spec coverage (§5.4):** tray mapping (already), tooltip, entry/exit toasts → Task 3; webhooks verdict-only pass + `dns` payload + resolver-target filter (already via `IsSideSubject`, tested as a kind-level check) → Task 1; MQTT `InternetPayload` (already), `dns` binary with discovery/availability/attributes, verdict `dnsBlame`, per-target scope attributes (already) → Task 2. §7 "Notifiers" bullets all have a test. §5.5 and §5.1 were earlier plans.
- **Type consistency:** `TransitionReason.Dns` (Task 1) is read by `MqttNotifier` (Task 2); `DnsBlame`/`DnsBlameKind` live in `ConEngine.Topology`; `DnsSummary`/`DnsSummaryState` in `ConEngine.Dns`; `TrayInfo.Verdict`/`NamesFailing` (Task 3) are used by both `TrayToasts.Internet` and `Program.cs`.
- **Review Focus:** 1 → Task 1; 2, 3 → Task 2; 4, 5 → Task 3.
- **Deliberate choices:** `Unknown` while watched reads ON (startup should not look like an outage); not-watched is availability, not state; the dns entity is republished on resolver-target transitions because the summary depends on them; `Program.cs` keeps one `TrayInfo` instead of a bare state so the Down-kind change is visible.
