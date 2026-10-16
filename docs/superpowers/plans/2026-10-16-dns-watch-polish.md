# DNS Watch — Polish (follow-ups 1–5) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the five small follow-ups the Plan 3 and Plan 4 reviews left open: a resolver row that reads "Down · Verified" during warm-up, the MQTT dns attributes lagging a Filtered change, stale retained MQTT topics for a target that used to be slugged `internet` or `dns`, DoH detection missing Windows' global policy and IPv6 zone ids, and the DNS page's watch switch staying flipped after a failed save.

**Architecture:** Each fix stays inside the component the review named. The status builder gets a pure `ResolverDetail` rule (last failure while not answering). The MQTT notifier gets a one-minute tick that republishes the dns entity only when its payload changed, and clears the per-target state/attribute topics of the reserved slugs on every refresh. The DoH detector grows from "a list of per-interface addresses" to a small registry view (per-interface servers, the `DoHPolicy` policy value, `EnableAutoDoh`, the well-known server list) with a pure decision function. The watch switch gets its own fetch handler with a revert path.

**Tech Stack:** .NET 10, xUnit; vanilla JS on the Razor DNS page (no JS harness — the controller verifies it in the embedded browser).

**Spec:** `docs/superpowers/specs/2026-10-10-dns-watch-design.md` §4.5 (`dns-over-https`), §5.2 (dashboard resolver lines), §5.3 (config page), §5.4 (MQTT). The follow-up list is in the Plan 3/4 ledgers (summarised in this plan's task texts). Master is at ab510fb (pushed).

## Global Constraints

- **Resolver detail** (spec §5.2): a resolver line shows its last detail only when it is not Responding. Rule for this plan: while the monitor is not answering (`TopologyCoordinator.Answering(state) == false`), the detail is the message of the **most recent failed** DNS result; if every recent result succeeded, the most recent message; `null` with no results or while answering. Nothing else in the status block changes.
- **MQTT** (spec §5.4): the dns entity's state, attributes and availability must follow the summary within **one minute** even when no transition fires (Filtered is decided from the result log). Republishing is suppressed when nothing changed (retained topics must not churn). On every refresh, the per-target topics `{base}/target/internet/*` and `{base}/target/dns/*` (state and attributes) are cleared with an empty retained message, because a pre-reservation node may have published them for a target named Internet or DNS; the fixed entities' own discovery topics are *not* cleared (they are republished in the same refresh). README gains one sentence about the suffixed entity on upgrade.
- **DoH** (spec §4.5): the `dns-over-https` warning fires for an enabled system resolver address when any of these holds: (a) a per-interface `Doh`/`Doh6` key for that address with non-zero `DohFlags` (today's rule); (b) the DNS client policy `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient` value `DoHPolicy` is **3 (Require)** — then every address counts; (c) `DoHPolicy` is **2 (Allow)** or `HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters` value `EnableAutoDoh` is **2**, and the address is a **well-known DoH server**: the built-in list (Cloudflare `1.1.1.1`, `1.0.0.1`, `2606:4700:4700::1111`, `2606:4700:4700::1001`; Google `8.8.8.8`, `8.8.4.4`, `2001:4860:4860::8888`, `2001:4860:4860::8844`; Quad9 `9.9.9.9`, `149.112.112.112`, `2620:fe::fe`, `2620:fe::9`) plus the subkey names of `Dnscache\Parameters\DohWellKnownServers`. `DoHPolicy` **1 (Prohibit)** disables (b) and (c) but not (a). Registry key names carrying an IPv6 zone id (`fe80::1%12`) match the resolver address with the zone stripped. The value meanings for `DoHPolicy` (1 Prohibit, 2 Allow, 3 Require) and `EnableAutoDoh` (2 = automatic DoH for well-known servers) are taken from Microsoft's policy reference and community documentation; the code states that assumption in one comment. Everything stays fail-safe: any registry error reads as "nothing configured".
- **DNS page** (spec §5.3): the Watch switch saves on change; when the save fails the switch and its hidden value revert and the error shows in `#watch-errors`; on success the page reloads. No new routes.
- Repo rules: tests wait for conditions, never sleep-then-assert; no personal addresses (documentation ranges and the public resolvers above only); commit per task; never push; no attribution lines in commits; output pristine apart from NU1903 and the pre-existing CS8631/CA2022/CA1416 in untouched test files.
- Test commands: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test Tests/ConNotify.Tests`, `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DohDetectorTests"`, `dotnet test ConnectionWatcher.sln`.

## Review Focus

1. **A resolver that is Down but whose newest result is a success** must show the last failure, never "Verified"; a resolver that is Recovering (answering) shows no detail. Task 1 tests both.
2. **The MQTT tick must not republish when nothing changed**: a tick with an unchanged summary publishes nothing (retained-topic churn would spam HA's logbook). Task 2 tests the change detector as a pure comparison.
3. **`DoHPolicy` Prohibit (1) with `EnableAutoDoh` 2** must report no well-known server (policy wins), while a per-interface entry still reports. Task 3 tests it.
4. **A resolver address that is not well-known under Allow** must not be reported. Task 3 tests it.
5. **The watch switch with a server error** must land back in its previous position with the error visible, not flipped. Task 4 has no harness; the controller verifies it in the browser with the preview server by forcing a 4xx (an invalid value is not reachable from the switch, so the check uses a temporarily unreachable endpoint or a browser-devtools request block) — at minimum, the handler is read against `site-config.js`'s `apiFetch` contract.

## File Structure

**Modify**
- `Programs/ConWatch/StatusSnapshotBuilder.cs` — `ResolverDetail(HostState, IReadOnlyList<DnsResult>)`.
- `Libraries/ConNotify/MqttNotifier.cs` — tick, change detection, reserved-slug cleanup; `Libraries/ConNotify/Builders.cs` — `DnsPublication` record.
- `Libraries/ConInfrastructure/Dns/DohConfiguration.cs` — `DohRegistryView`, `WindowsDohDetector.Decide`, registry reads for policy/auto/well-known.
- `Programs/ConWatch/wwwroot/lib/site-dns.js`, `Programs/ConWatch/Pages/Config/Dns.cshtml` — watch switch handler.
- `README.md` — one sentence in Notifications.
- Tests: `Tests/ConWatch.Tests/StatusResolverDetailTests.cs` (new), `Tests/ConNotify.Tests/BuildersTests.cs`, `Tests/ConNotify.Tests/MqttNotifierInboxTests.cs`, `Tests/ConEngine.Tests/DohDetectorTests.cs`.

---

### Task 1: A resolver that isn't answering shows its last failure

**Files:**
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs` (the `Resolver` local in `Build`, ~line 95-98)
- Test: `Tests/ConWatch.Tests/StatusResolverDetailTests.cs` (new)

**Interfaces:**
- Produces: `public static string? StatusSnapshotBuilder.ResolverDetail(HostState state, IReadOnlyList<ConEngine.Dns.DnsResult> recent)`.

- [ ] **Step 1: Write the failing test**

```csharp
using ConCore;
using ConEngine.Dns;

namespace ConWatch.Tests;

public class StatusResolverDetailTests
{
    private static DnsResult R(int minute, bool success, string message) =>
        new(DateTimeOffset.Parse("2026-10-16T00:00:00Z").AddMinutes(minute), success,
            DnsResultLog.Parse(message).Outcome, DnsResultLog.Parse(message).Name, message);

    [Fact]
    public void A_down_resolver_shows_its_last_failure_even_when_the_newest_sample_succeeded() // Review Focus 1
    {
        var recent = new[] { R(0, false, "TimedOut (dns.google)"), R(1, false, "ServFail (one.one.one.one)"), R(2, true, "Verified (dns.google)") };
        Assert.Equal("ServFail (one.one.one.one)", StatusSnapshotBuilder.ResolverDetail(HostState.Down, recent));
    }

    [Fact]
    public void With_only_successes_the_newest_message_is_shown_and_an_answering_resolver_shows_none()
    {
        var recent = new[] { R(0, true, "Verified (dns.google)"), R(1, true, "Verified (dns.quad9.net)") };
        Assert.Equal("Verified (dns.quad9.net)", StatusSnapshotBuilder.ResolverDetail(HostState.Starting, recent));
        Assert.Null(StatusSnapshotBuilder.ResolverDetail(HostState.Recovering, recent));
        Assert.Null(StatusSnapshotBuilder.ResolverDetail(HostState.Responding, recent));
        Assert.Null(StatusSnapshotBuilder.ResolverDetail(HostState.Down, []));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~StatusResolverDetailTests"`
Expected: FAIL (compile: `ResolverDetail` missing).

- [ ] **Step 3: Implement**

In `StatusSnapshotBuilder`:

```csharp
    /// <summary>The detail beside a resolver that isn't answering: its most recent failed
    /// DNS result, so a Down row never reads "Verified" because one later probe got through
    /// while the state still lags a burst; the newest message when nothing failed; null while
    /// answering or without samples (spec 2026-10-10 §5.2).</summary>
    public static string? ResolverDetail(HostState state, IReadOnlyList<ConEngine.Dns.DnsResult> recent)
    {
        if (recent.Count == 0 || TopologyCoordinator.Answering(state))
            return null;
        for (var i = recent.Count - 1; i >= 0; i--)
            if (!recent[i].Success)
                return recent[i].Message;
        return recent[^1].Message;
    }
```

and the `Resolver` local becomes:

```csharp
        StatusResolver Resolver(TargetMonitor m) => new(m.Target.Id, m.Target.Address, m.Target.Name,
            m.CurrentState.ToString(), ResolverDetail(m.CurrentState, engine.DnsResults.Recent(m.Target.Id)), m.Target.Enabled);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~StatusResolverDetailTests|FullyQualifiedName~DnsStatusTests|FullyQualifiedName~StatusShapeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/StatusSnapshotBuilder.cs Tests/ConWatch.Tests/StatusResolverDetailTests.cs
git commit -m "fix(status): a resolver that isn't answering shows its last failure, never a later Verified"
```

---

### Task 2: The MQTT dns entity follows the summary within a minute; reserved-slug leftovers are cleared

**Files:**
- Modify: `Libraries/ConNotify/Builders.cs`, `Libraries/ConNotify/MqttNotifier.cs`, `README.md`
- Test: `Tests/ConNotify.Tests/BuildersTests.cs`, `Tests/ConNotify.Tests/MqttNotifierInboxTests.cs`

**Interfaces:**
- Produces (`ConNotify`): `public sealed record DnsPublication(string Availability, string State, string Attributes)` with `public static DnsPublication From(WatcherEngine engine)` (availability/state/attributes strings exactly as `PublishDnsAsync` computes them today); `MqttNotifier.Inbox.TickMessage` and `Inbox.Tick()`; `MqttNotifier.DnsRefreshInterval = TimeSpan.FromMinutes(1)`.

- [ ] **Step 1: Write the failing tests**

Append to `HaDiscoveryTests` in `BuildersTests.cs`:

```csharp
    [Fact]
    public void Dns_publication_equality_is_what_the_tick_uses_to_skip_unchanged_republishes() // Review Focus 2
    {
        var a = new DnsPublication("online", "ON", "{\"state\":\"Ok\"}");
        var same = new DnsPublication("online", "ON", "{\"state\":\"Ok\"}");
        var filtered = a with { Attributes = "{\"state\":\"Filtered\"}" };
        Assert.Equal(a, same);
        Assert.NotEqual(a, filtered);
    }
```

Append to `MqttNotifierInboxTests`:

```csharp
    [Fact]
    public async Task A_tick_is_delivered_in_order_with_traffic()
    {
        var inbox = new MqttNotifier.Inbox();
        inbox.Post(Transition("A"));
        inbox.Tick();
        Assert.IsType<TargetTransition>(await inbox.ReadAsync(CancellationToken.None));
        Assert.IsType<MqttNotifier.Inbox.TickMessage>(await inbox.ReadAsync(CancellationToken.None));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConNotify.Tests`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`Builders.cs` (after `DnsResolverEntry`):

```csharp
/// <summary>Everything the dns entity publishes, so a periodic tick can tell "unchanged" from
/// "the summary moved without a transition" (Filtered is decided from the result log).</summary>
public sealed record DnsPublication(string Availability, string State, string Attributes)
{
    public static DnsPublication From(WatcherEngine engine)
    {
        var watch = engine.DnsWatch;
        var summary = DnsSummary.Compute(watch, engine.DnsResults);
        return new(StateMapping.DnsAvailabilityPayload(summary.Watched), StateMapping.DnsPayload(summary.State),
            HaDiscovery.DnsAttributes(summary, engine.Verdict?.Current.Dns,
                watch.System.Select(DnsResolverEntry.From).ToList(), watch.Upstream.Select(DnsResolverEntry.From).ToList()));
    }
}
```

(`using ConEngine;` for `WatcherEngine`.)

`MqttNotifier.cs`:
- `Inbox`: add `public sealed record TickMessage;` and `public void Tick() => _queue.Writer.TryWrite(new TickMessage());`.
- `public static readonly TimeSpan DnsRefreshInterval = TimeSpan.FromMinutes(1);` and a field `private DnsPublication? _lastDns;`.
- `PublishDnsAsync(client, engine, ct, bool onlyIfChanged = false)`: compute `var next = DnsPublication.From(engine);` if `onlyIfChanged && next == _lastDns` return; publish the three topics from `next` (retained), then `_lastDns = next;`. (`_lastDns` is only touched on the session loop's thread; reset it to null at the start of `RunConnectedSessionAsync` so a reconnect republishes.)
- In `RunConnectedSessionAsync`, after the initial `RefreshAllAsync`, start a ticker bound to the session: `using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken); var ticker = Task.Run(async () => { using var timer = new PeriodicTimer(DnsRefreshInterval); try { while (await timer.WaitForNextTickAsync(tickCts.Token).ConfigureAwait(false)) _inbox.Tick(); } catch (OperationCanceledException) { } }, CancellationToken.None);` and in the `finally`, `tickCts.Cancel();` before the goodbye. Handle `case Inbox.TickMessage:` by reading `_engine` under `_lock` and, if non-null, `await PublishDnsAsync(client, engine, stoppingToken, onlyIfChanged: true)`. `WaitForReconfigureAsync` already discards everything but a reconfigure, so ticks posted while disconnected are harmless (the queue drops oldest at 500).
- In `RefreshAllAsync`, before the stale-slug loop:

```csharp
        // A node from before the slugs were reserved may have published a target named
        // Internet or DNS under these per-target topics; its discovery topic is the fixed
        // entity's own and is republished below, but these two would otherwise sit retained forever.
        foreach (var reserved in MqttTopics.ReservedSlugs)
        {
            await PublishAsync(client, _topics.TargetState(reserved), "", retain: true, ct);
            await PublishAsync(client, _topics.TargetAttributes(reserved), "", retain: true, ct);
        }
```

README, in the Notifications MQTT paragraph after the sentence about retained state topics: "A target named *Internet* or *DNS* gets a suffixed entity (`internet_2`, `dns_2`) so it cannot overwrite the fixed sensors; after upgrading, delete the old entity in Home Assistant if one lingers."

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConNotify.Tests` and `dotnet build Libraries/ConNotify`
Expected: PASS, no new warnings.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConNotify/Builders.cs Libraries/ConNotify/MqttNotifier.cs Tests/ConNotify.Tests/BuildersTests.cs Tests/ConNotify.Tests/MqttNotifierInboxTests.cs README.md
git commit -m "fix(mqtt): the dns entity follows the summary within a minute; reserved-slug leftovers are cleared on refresh"
```

---

### Task 3: DoH detection reads the global policy, auto-DoH and well-known servers; zone ids are stripped

**Files:**
- Modify: `Libraries/ConInfrastructure/Dns/DohConfiguration.cs`
- Test: `Tests/ConEngine.Tests/DohDetectorTests.cs`

**Interfaces:**
- Produces (`ConInfrastructure.NameResolution`): `public sealed record DohRegistryView(IReadOnlyList<string> PerInterface, int? DohPolicy, int? EnableAutoDoh, IReadOnlyList<string> WellKnown) { public static DohRegistryView Empty; }`; `WindowsDohDetector(Func<DohRegistryView> view)` replaces the `Func<IEnumerable<string>>` constructor (update the existing tests' constructor calls to `() => new DohRegistryView([...], null, null, [])`); `public static IReadOnlyList<string> WindowsDohDetector.Decide(DohRegistryView view, IEnumerable<string> resolverAddresses)` (pure); `public static readonly IReadOnlyList<string> WindowsDohDetector.BuiltInWellKnown`; `AddressesFrom` unchanged.

- [ ] **Step 1: Write the failing tests**

Replace the constructor calls in the existing tests (`new WindowsDohDetector(() => ["1.1.1.1", "FE80::1"])` → `new WindowsDohDetector(() => new DohRegistryView(["1.1.1.1", "FE80::1"], null, null, []))`, the throwing source likewise) and append:

```csharp
    private static DohRegistryView View(string[]? perInterface = null, int? policy = null, int? auto = null, string[]? wellKnown = null) =>
        new(perInterface ?? [], policy, auto, wellKnown ?? []);

    [Fact]
    public void Require_policy_reports_every_resolver()
    {
        Assert.Equal(["10.1.10.1", "1.1.1.1"], WindowsDohDetector.Decide(View(policy: 3), ["10.1.10.1", "1.1.1.1"]));
    }

    [Fact]
    public void Allow_policy_or_auto_doh_reports_only_well_known_servers() // Review Focus 4
    {
        Assert.Equal(["1.1.1.1"], WindowsDohDetector.Decide(View(policy: 2), ["10.1.10.1", "1.1.1.1"]));
        Assert.Equal(["8.8.8.8"], WindowsDohDetector.Decide(View(auto: 2), ["192.0.2.53", "8.8.8.8"]));
        Assert.Equal(["2620:fe::fe"], WindowsDohDetector.Decide(View(auto: 2), ["2620:fe::fe"]));
        Assert.Empty(WindowsDohDetector.Decide(View(auto: 1), ["8.8.8.8"]));
        Assert.Empty(WindowsDohDetector.Decide(View(), ["8.8.8.8"]));
    }

    [Fact]
    public void A_registry_listed_well_known_server_counts_under_allow()
    {
        Assert.Equal(["192.0.2.53"], WindowsDohDetector.Decide(View(auto: 2, wellKnown: ["192.0.2.53"]), ["192.0.2.53", "192.0.2.54"]));
    }

    [Fact]
    public void Prohibit_policy_switches_off_auto_doh_but_not_a_per_interface_entry() // Review Focus 3
    {
        Assert.Empty(WindowsDohDetector.Decide(View(policy: 1, auto: 2), ["8.8.8.8"]));
        Assert.Equal(["10.1.10.1"], WindowsDohDetector.Decide(View(perInterface: ["10.1.10.1"], policy: 1, auto: 2), ["10.1.10.1", "8.8.8.8"]));
    }

    [Fact]
    public void A_zone_id_on_a_registry_key_does_not_stop_the_match()
    {
        Assert.Equal(["fe80::1"], WindowsDohDetector.Decide(View(perInterface: ["fe80::1%12"]), ["fe80::1"]));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DohDetectorTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

```csharp
/// <summary>What the Windows registry says about DoH, read once per evaluation: the per-interface
/// servers with DoH on, the DNS client policy (DoHPolicy: 1 Prohibit, 2 Allow, 3 Require — the
/// numeric meanings follow Microsoft's policy reference), EnableAutoDoh (2 = automatic DoH for
/// well-known servers) and the admin-added well-known servers.</summary>
public sealed record DohRegistryView(IReadOnlyList<string> PerInterface, int? DohPolicy, int? EnableAutoDoh, IReadOnlyList<string> WellKnown)
{
    public static DohRegistryView Empty { get; } = new([], null, null, []);
}
```

`WindowsDohDetector(Func<DohRegistryView> view)`; parameterless constructor `: this(ReadRegistry)`. `AddressesUsingDoh` becomes `try { return Decide(view(), resolverAddresses); } catch (Exception) { return []; }`.

```csharp
    /// <summary>Windows' own list of DoH-capable public resolvers (Cloudflare, Google, Quad9).</summary>
    public static readonly IReadOnlyList<string> BuiltInWellKnown =
    [
        "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001",
        "8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844",
        "9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9",
    ];

    public static IReadOnlyList<string> Decide(DohRegistryView view, IEnumerable<string> resolverAddresses)
    {
        var perInterface = view.PerInterface.Select(Canonical).ToHashSet();
        var require = view.DohPolicy == 3;
        var autoForWellKnown = view.DohPolicy != 1 && (view.DohPolicy == 2 || view.EnableAutoDoh == 2);
        var wellKnown = BuiltInWellKnown.Concat(view.WellKnown).Select(Canonical).ToHashSet();
        return resolverAddresses
            .Where(a => { var c = Canonical(a); return require || perInterface.Contains(c) || (autoForWellKnown && wellKnown.Contains(c)); })
            .Select(a => a.ToLowerInvariant()).Distinct().ToList();
    }

    /// <summary>One spelling per address: zone id stripped (a registry key may carry it),
    /// IPv6 compressed, else lower-cased.</summary>
    private static string Canonical(string address)
    {
        var bare = address.Split('%')[0];
        return System.Net.IPAddress.TryParse(bare, out var ip) ? ip.ToString() : bare.ToLowerInvariant();
    }

    private static DohRegistryView ReadRegistry()
    {
        if (!OperatingSystem.IsWindows())
            return DohRegistryView.Empty;
        // per-interface: today's walk → List<string> perInterface = AddressesFrom(rows)
        using var policyKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient");
        var policy = policyKey?.GetValue("DoHPolicy") as int?;
        using var parameters = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters");
        var auto = parameters?.GetValue("EnableAutoDoh") as int?;
        var wellKnown = new List<string>();
        using var wk = parameters?.OpenSubKey("DohWellKnownServers");
        if (wk is not null)
            wellKnown.AddRange(wk.GetSubKeyNames());
        return new DohRegistryView(perInterface, policy, auto, wellKnown);
    }
```

(Keep the existing per-interface walk as the first half of `ReadRegistry`, yielding `perInterface`. A `GetValue` that returns a `long` for a REG_QWORD is not an `int?`; that is acceptable for these DWORD values, and `AddressesFrom` already handles both for `DohFlags`.) Update the class summary comment to list the three sources.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DohDetectorTests"` and `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~ConfigHealthTests"` (uses `FakeDoh`, unaffected) and `dotnet build Libraries/ConInfrastructure`
Expected: PASS, no new warnings.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConInfrastructure/Dns/DohConfiguration.cs Tests/ConEngine.Tests/DohDetectorTests.cs
git commit -m "fix(health): dns-over-https also reads the DNS client policy, auto-DoH and well-known servers; zone ids stripped"
```

---

### Task 4: The watch switch reverts when its save fails

**Files:**
- Modify: `Programs/ConWatch/wwwroot/lib/site-dns.js` (the watch-switch block, ~lines 19-25), `Programs/ConWatch/Pages/Config/Dns.cshtml` (only if the hidden input or form id changes — it should not)

**Interfaces:**
- Consumes: `apiFetch(method, url, body)` → `{ ok, status, body }`, `showErrors(listEl, result)`, `clearErrors(listEl)` from `site-config.js`.

- [ ] **Step 1: Implement**

Replace the `wireSettingsForm("watch-form", "watch-errors");` line and the `change` listener with:

```js
// The Watch switch saves on change, like the resolver switches. A failed save puts the
// switch back where it was and shows why; a successful one reloads the page.
document.getElementById("dns-enabled").addEventListener("change", async (e) => {
    const errors = document.getElementById("watch-errors");
    clearErrors(errors);
    const hidden = document.querySelector("#watch-form [name='Dns.Enabled']");
    const previous = hidden.value;
    hidden.value = e.target.checked ? "true" : "false";
    const result = await apiFetch("PUT", "/api/v1/settings", { "Dns.Enabled": hidden.value });
    if (result.ok) { location.reload(); return; }
    hidden.value = previous;
    e.target.checked = previous === "true";
    showErrors(errors, result);
});
document.getElementById("watch-form").addEventListener("submit", (e) => e.preventDefault());
```

- [ ] **Step 2: Verify**

Run the DNS page tests (they render the page, not the script): `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsPageTests"`. The controller then checks the switch in the embedded browser against the preview server: toggling saves and reloads; with the admin lock engaged in another way (or the server stopped between load and click), the switch snaps back and the error list shows the failure.

- [ ] **Step 3: Commit**

```bash
git add Programs/ConWatch/wwwroot/lib/site-dns.js
git commit -m "fix(config): the DNS watch switch reverts when its save fails"
```

---

### Task 5: Whole-branch gate

- [ ] Run `dotnet test ConnectionWatcher.sln` (allow 10 minutes; the known flake `EndToEndTests.Verdict_walks_the_full_scenario_and_history_records_it` may fail under load — re-run alone if it is the only failure) and `dotnet build Programs/ConWatchTray`. Expected: 0 failures, no new warnings. Nothing to commit.

---

## Self-review notes

- **Coverage:** follow-up 1 → Task 1; 2 and 3 → Task 2; 4 → Task 3; 5 → Task 4. Each binding rule in Global Constraints maps to a task.
- **Type consistency:** `DnsPublication.From(WatcherEngine)` is used only by `MqttNotifier.PublishDnsAsync`; `DohRegistryView` is the only constructor input to `WindowsDohDetector` and the existing `AddressesFrom`/`DohDetectorTests` fakes are updated in Task 3; `ResolverDetail` is the only caller change in the status builder.
- **Review Focus:** 1 → Task 1; 2 → Task 2; 3, 4 → Task 3; 5 → Task 4 (browser check by the controller).
- **Assumptions stated:** the `DoHPolicy`/`EnableAutoDoh` numeric meanings (Microsoft's policy page names the options Prohibit/Allow/Require; the DWORD mapping and `EnableAutoDoh = 2` come from community documentation) — if Mark knows otherwise, Task 3's constants are the only place to change.
