# DNS Watch — Plan 2: Verdict, Notes and Status — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the Plan 1 plumbing into the user-visible answer: a `DnsDown` verdict with a blame line when the anchors answer but every system resolver is Down, a `Filtered` state for blocklisting resolvers, five admin notes, a `dns` block in status and MCP, two config-health codes, a metrics gauge, an ISP-report line, and the three one-line consumer hooks (banner, tray colour, MQTT internet binary) that keep edge users from seeing a verdict nothing renders.

**Architecture:** The DNS probe's `PingResult.Message` gains the asked name and a `Filtered` outcome, and the engine's `DnsResultLog` parses both. A new `ConEngine.Dns.DnsSummary` computes the `dns` state (`Ok / Degraded / Filtered / Down / Unknown`) from the Resolver-scope monitors and the log, and everything that reports it (status, MCP, metrics, notes) calls that one function. `VerdictCoordinator` gains the `DnsDown` rule between reachability and quality; `ModelCheckCoordinator` gains five resolver note kinds; the reconciler deletes a managed resolver whose address is gone (D10) instead of keeping it disabled.

**Tech Stack:** .NET 10, EF Core (no migration in this plan), xUnit with `FakeTimeProvider`, React + vitest for the banner case.

**Spec:** `docs/superpowers/specs/2026-10-10-dns-watch-design.md` (§4.2 hit/miss incl. D11, §4.3 D10, §4.4, §4.5, §5.1, §5.4 only the three one-liners, rollout step 2). Plan 1 (`docs/superpowers/plans/2026-10-14-dns-watch-plumbing.md`) is merged; its namespaces are `ConInfrastructure.NameResolution` and `ConWatch.NameResolution`.

## Global Constraints

- `VerdictState.DnsDown` ranks above `Healthy`, `SiteIncident`, `InternetWeather`, `LinkDegraded` and below `LinkDown`: in `Classify`, the DNS check runs after reachability is proven and before `Quality`. `Map(DnsDown)` is `HostState.Down`.
- `DnsDown` rule (spec §4.4): a counted anchor answers; the watch is on (names non-empty); at least one enabled system resolver is determined; every enabled system resolver is `Down`. `Starting`, `Faltering`, `Recovering` never count as Down. While some system resolvers are undetermined and the rest are Down, wait under the existing `StillWaiting`/`AnchorWaitCap` machinery (`Unknown`, "Waiting for resolvers to report").
- Blame: any upstream resolver answering → `YourResolver`, message `Your resolver {address} isn't answering, but {upstream address} answers directly.`; none answering or none configured → `NoResolver`, message `No resolver is answering, though the internet is reachable.` plus, when none is configured, ` Name a public resolver in Config → DNS so ConnectionWatcher can tell your resolver from everyone's.`
- `LinkDown` detail gains the suffix ` — name lookups are failing too` when every enabled system resolver is Down.
- D11: for a seeded name, a blocked answer (every A record is `0.0.0.0`, `::` or `127.0.0.1`; or NXDOMAIN; or an empty NOERROR answer) is a **hit** with `Message` `Filtered: {detail} ({name})`. A real address outside the expected set stays a `WrongAnswer: {detail} ({name})` miss.
- DNS `PingResult.Message` vocabulary, now with the asked name in trailing parentheses: `Answered (dns.google)`, `NxDomain (example.org)`, `Filtered: NXDOMAIN (dns.google)`, `WrongAnswer: 10.0.0.1 (dns.google)`, `ServFail (…)`, `RcodeRefused (…)`, `Rcode{n} (…)`, `TimedOut (…)`, `Malformed (…)`, `BadName (…)`, a `SocketError` name ` (…)`; `NoLookupNames` has no name. Never the bare word `Refused`.
- `dns` summary state: `Unknown` (no enabled system resolver determined), `Down` (all Down), `Degraded` (some Down, or any Faltering/Recovering), `Filtered` (every answering system resolver has ≥ 3 `Filtered` seeded-name results among its last 10 and no `Answered` one), else `Ok`; `watched` is false and state `Unknown` when the watch is off or the name list is empty.
- Note kinds and keys (spec §4.5): `ResolverPartial` `resolver-partial:{id}`, `ResolverLying` `resolver-lying:{id}` (3 of last 10 `WrongAnswer`), `ResolverFiltered` `resolver-filtered:{id}` (3 of last 10 seeded-name results `Filtered`, none `Answered`), `UpstreamResolverDown` `upstream-resolver-down:{id}`, `NameUnresolvable` `name-unresolvable` (one name missed its last 3 asks on every answering resolver while another name hit).
- Config-health codes: `no-upstream-resolver`, `dns-not-watched`. `dns-over-https` is deferred to Plan 3 (the DNS panel), where Windows' DoH configuration can be shown with the resolver rows.
- D10: a managed resolver whose address is gone from the OS list is **deleted** by the reconciler; the retired set only tracks present resolvers disabled because the watch is off.
- ISP report: `DnsDown` time is its own `DnsDownTime` figure and report line, never in `LinkDownTime`, `LinkDegradedTime`, `WeatherTime` or the attributed episodes.
- Status: `StatusSnapshot` gains a trailing `Dns` member (`StatusDns?`), null when the engine is absent; `StatusSnapshot.Disabled` serializes with `"dns":null`. MCP `get_current_status` gains `dns`.
- Repo rules: tests wait for conditions, never sleep-then-assert; no personal endpoints; commit per task; never push; write files as UTF-8 via the Edit tool.
- Test commands: `dotnet test Tests/<Project> --filter "FullyQualifiedName~<Class>"`; client `npm --prefix Programs/ConWatch/ClientApp test -- --run`.

## Review Focus

1. **A resolver that answers the seeded names with `0.0.0.0` but answers hostname targets normally** must read as `Filtered`, keep the verdict `Healthy`, and raise `ResolverFiltered`, never `DnsDown`. Task 3 and Task 5 test it.
2. **Anchors Down and resolvers Down together** must be `LinkDown` with the "name lookups are failing too" suffix, never `DnsDown`. Task 4 tests it.
3. **The watch turned off while a system resolver target still exists** (disabled by the reconciler) must give `dns.watched == false`, state `Unknown`, no `DnsDown`, no notes. Task 3 (summary) and Task 4 (verdict ignores disabled resolvers) test it.
4. **A single upstream resolver down while the system resolver is fine** is an `UpstreamResolverDown` note and nothing else. Task 5 tests it.
5. **A managed resolver whose address changes at DHCP renew** must be deleted and a fresh one created, with no `Dns.RetiredResolvers` entry left behind. Task 2 tests it.

## File Structure

**Create**
- `Core/ConEngine/Dns/DnsSummary.cs` — the `dns` state computation, one function, shared by status, MCP, metrics and notes.
- `Core/ConEngine/Dns/DnsWatch.cs` — the resolver monitors and the watch flag handed to the verdict and the model check.
- `Tests/ConEngine.Tests/DnsSummaryTests.cs`, `Tests/ConEngine.Tests/VerdictDnsDownTests.cs`, `Tests/ConEngine.Tests/ResolverNotesTests.cs`.

**Modify**
- `Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs` — `Filtered`, the name suffix.
- `Core/ConEngine/Dns/DnsResultLog.cs` — parse outcome and name.
- `Programs/ConWatch/Dns/SystemResolverReconciler.cs`, `SystemResolverService.cs` — D10.
- `Core/ConEngine/Configuration/VerdictOptions.cs`, `Core/ConEngine/Topology/VerdictCoordinator.cs`, `Core/ConEngine/WatcherEngine.cs`, `Programs/ConWatch/WatcherHostService.cs` — `DnsDown`.
- `Core/ConEngine/Topology/ModelCheckCoordinator.cs`, `Libraries/ConInfrastructure/Tray/TrayStatus.cs`, `Programs/ConWatch/ClientApp/src/lib/types.ts` — notes.
- `Programs/ConWatch/StatusSnapshotBuilder.cs`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`, `Programs/ConWatch/Api/MetricsEndpoint.cs`, `Libraries/ConPersistence/Config/ConfigHealth.cs` — status, MCP, metrics, health.
- `Libraries/ConPersistence/Report/IspReportBuilder.cs`, `Programs/ConWatch/Pages/Report.cshtml`, `Libraries/ConPersistence/Queries/TransitionDto.cs`, `HistoryQueries.cs` — report and history.
- `Programs/ConWatch/ClientApp/src/components/VerdictBanner.tsx`, `Libraries/ConInfrastructure/Tray/TrayStatus.cs`, `Libraries/ConNotify/Builders.cs`, `README.md` — the three one-liners and the README section.
- Tests touched: `DnsQueryTransceiverTests`, `DnsResultLogTests`, `SystemResolverReconcilerTests`, `SystemResolverServiceTests`, `ModelCheckCoordinatorTests`, `StatusShapeTests`, `McpEndpointTests`, `MetricsEndpointTests`, `ConfigHealthTests`, `IspReportBuilderTests`, `TransitionDtoTests`, `HistoryScopeTests`, `TrayStatusTests`, `VerdictBanner.test.ts`, ConNotify `HaDiscovery` tests.

---

### Task 1: `Filtered` outcome and the asked name in every DNS result

**Files:**
- Modify: `Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs` (`PingAsync` message sites, `Classify`)
- Modify: `Core/ConEngine/Dns/DnsResultLog.cs`
- Test: `Tests/ConEngine.Tests/DnsQueryTransceiverTests.cs`, `Tests/ConEngine.Tests/DnsResultLogTests.cs`

**Interfaces:**
- Produces: every DNS `PingResult.Message` except `NoLookupNames` ends with ` ({name})`. `DnsQueryTransceiver.Classify(IPAddress, DnsLookupName, DnsAnswer, long)` returns a success `Filtered: {detail} ({name})` for a blocked seeded answer. `ConEngine.Dns.DnsResult(DateTimeOffset At, bool Success, string Outcome, string? Name, string Message)` where `Outcome` is the token before the first space or colon (`Answered`, `Filtered`, `WrongAnswer`, `TimedOut`, …) and `Name` is the parenthesised suffix or null. `DnsResultLog.Record/Recent` unchanged in signature.

- [ ] **Step 1: Update the transceiver tests**

In `Tests/ConEngine.Tests/DnsQueryTransceiverTests.cs`, change every `Assert.Equal("<token>", result.Message)` for a result that asked a name to the suffixed form, e.g. `"Answered (dns.google)"`, `"WrongAnswer: 10.0.0.1 (dns.google)"`, `"NxDomain (example.org)"`, `"ServFail (example.org)"`, `"RcodeRefused (example.org)"`, `"Rcode4 (example.org)"`, `"TimedOut (example.org)"`, `"Malformed (example.org)"`, `"BadName (a..b)"`; `"NoLookupNames"` stays bare. Then add:

```csharp
    [Theory]
    [InlineData("0.0.0.0", "Filtered: 0.0.0.0 (dns.google)")]
    [InlineData("127.0.0.1", "Filtered: 127.0.0.1 (dns.google)")]
    public async Task A_blocked_address_for_a_seeded_name_is_a_Filtered_hit(string blocked, string message)
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse(blocked))];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public async Task NXDOMAIN_and_an_empty_answer_for_a_seeded_name_are_Filtered_hits()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NxDomain)];
        using var probe = Probe(server, Names(Google));
        var nx = await probe.PingAsync(2000);
        Assert.True(nx.Success);
        Assert.Equal("Filtered: NXDOMAIN (dns.google)", nx.Message);

        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError)];
        var empty = await probe.PingAsync(2000);
        Assert.True(empty.Success);
        Assert.Equal("Filtered: no address (dns.google)", empty.Message);
    }

    [Fact]
    public async Task A_real_wrong_address_is_still_a_WrongAnswer_miss()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("0.0.0.0"), IPAddress.Parse("10.0.0.1"))];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success); // one real address among the blocked ones: not a clean block
        Assert.Equal("WrongAnswer: 0.0.0.0, 10.0.0.1 (dns.google)", result.Message);
    }
```

(The existing `NXDOMAIN_is_a_miss_for_a_seeded_name_but_a_hit_for_an_admin_name` test changes: the seeded half now expects a `Filtered` hit; rename it `NXDOMAIN_is_Filtered_for_a_seeded_name_and_a_hit_for_an_admin_name`.)

- [ ] **Step 2: Update the result-log tests**

In `Tests/ConEngine.Tests/DnsResultLogTests.cs` replace the unit test with:

```csharp
    [Fact]
    public void Keeps_the_last_twenty_per_target_oldest_first_and_parses_outcome_and_name()
    {
        var log = new DnsResultLog();
        var id = Guid.NewGuid();
        var ip = IPAddress.Loopback;
        Assert.Empty(log.Recent(id));

        for (var i = 0; i < 25; i++)
            log.Record(id, new PingResult(ip, i % 2 == 0, 5, $"Answered (n{i}.test)", DateTimeOffset.UnixEpoch.AddSeconds(i)));

        var recent = log.Recent(id);
        Assert.Equal(DnsResultLog.Keep, recent.Count);
        Assert.Equal("n5.test", recent[0].Name);
        Assert.Equal("Answered", recent[0].Outcome);
        Assert.Equal("n24.test", recent[^1].Name);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(24), recent[^1].At);
        Assert.True(recent[^1].Success);
        Assert.Empty(log.Recent(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("Answered (dns.google)", "Answered", "dns.google")]
    [InlineData("Filtered: NXDOMAIN (dns.google)", "Filtered", "dns.google")]
    [InlineData("WrongAnswer: 10.0.0.1, 10.0.0.2 (one.one.one.one)", "WrongAnswer", "one.one.one.one")]
    [InlineData("TimedOut (example.org)", "TimedOut", "example.org")]
    [InlineData("NoLookupNames", "NoLookupNames", null)]
    [InlineData("ConnectionReset (dns.quad9.net)", "ConnectionReset", "dns.quad9.net")]
    public void Outcome_and_name_are_parsed_from_the_message(string message, string outcome, string? name)
    {
        var (parsedOutcome, parsedName) = DnsResultLog.Parse(message);
        Assert.Equal(outcome, parsedOutcome);
        Assert.Equal(name, parsedName);
    }
```

- [ ] **Step 3: Run both test classes to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsQueryTransceiverTests|FullyQualifiedName~DnsResultLogTests"`
Expected: FAIL (messages lack the suffix; `DnsResult.Outcome`/`Name` and `DnsResultLog.Parse` do not exist).

- [ ] **Step 4: Implement the transceiver change**

In `DnsQueryTransceiver.PingAsync`, after `var lookup = names.Next(ipAddress);` and the `NoLookupNames` return, every `new PingResult(ipAddress, false, 0, "<token>")` becomes `Miss("<token>")` using a local function `PingResult Miss(string token) => new(ipAddress, false, 0, $"{token} ({lookup.Name})");` (the `SocketException` arm uses `Miss(ex.SocketErrorCode.ToString())`, the generic catch `Miss(ex.Message)`, the `ArgumentException` arm `Miss("BadName")`). Replace `Classify` with:

```csharp
    private static readonly string[] BlockedAddresses = ["0.0.0.0", "::", "127.0.0.1"];

    /// <summary>Spec §4.2 / D11. A seeded name must come back with an expected address.
    /// A blocked answer (every address is 0.0.0.0, :: or 127.0.0.1; NXDOMAIN; or an
    /// empty answer) is a working resolver that filters: a hit, "Filtered". Any other
    /// address outside the expected set is interception: a miss, "WrongAnswer".
    /// An admin name has no expected set, so any real answer is a hit.</summary>
    internal static PingResult Classify(IPAddress resolver, DnsLookupName lookup, DnsAnswer answer, long elapsedMs)
    {
        var suffix = $" ({lookup.Name})";
        if (lookup.HasExpected)
        {
            if (answer.Rcode == DnsRcode.NoError && answer.Addresses.Any(a => lookup.Expected.Contains(a.ToString())))
                return new PingResult(resolver, true, elapsedMs, "Answered" + suffix);
            var detail = answer.Rcode switch
            {
                DnsRcode.NoError when answer.Addresses.Count > 0 => string.Join(", ", answer.Addresses),
                DnsRcode.NoError => "no address",
                DnsRcode.NxDomain => "NXDOMAIN",
                DnsRcode.ServFail => "SERVFAIL",
                DnsRcode.Refused => "REFUSED",
                var other => $"RCODE {(int)other}",
            };
            var blocked = answer.Rcode == DnsRcode.NxDomain ||
                (answer.Rcode == DnsRcode.NoError && answer.Addresses.All(a => BlockedAddresses.Contains(a.ToString())));
            if (blocked)
                return new PingResult(resolver, true, elapsedMs, $"Filtered: {detail}{suffix}");
            return answer.Rcode == DnsRcode.NoError
                ? new PingResult(resolver, false, 0, $"WrongAnswer: {detail}{suffix}")
                : Miss(answer.Rcode);
        }
        return answer.Rcode switch
        {
            DnsRcode.NoError => new PingResult(resolver, true, elapsedMs, "Answered" + suffix),
            DnsRcode.NxDomain => new PingResult(resolver, true, elapsedMs, "NxDomain" + suffix),
            var rcode => Miss(rcode),
        };

        PingResult Miss(DnsRcode rcode) => new(resolver, false, 0, (rcode switch
        {
            DnsRcode.ServFail => "ServFail",
            DnsRcode.Refused => "RcodeRefused",
            var other => $"Rcode{(int)other}",
        }) + suffix);
    }
```

(An empty NOERROR answer: `Addresses.All(...)` over an empty list is true, so it is `Filtered: no address`, as the spec says.)

- [ ] **Step 5: Implement the log change**

`Core/ConEngine/Dns/DnsResultLog.cs`:

```csharp
using ConCore;

namespace ConEngine.Dns;

/// <summary>One DNS probe outcome. Outcome is the message's leading token
/// ("Answered", "Filtered", "WrongAnswer", "TimedOut", …); Name is the asked
/// lookup name, null for a probe that sent nothing.</summary>
public sealed record DnsResult(DateTimeOffset At, bool Success, string Outcome, string? Name, string Message);

/// <summary>
/// The last <see cref="Keep"/> DNS results per Resolver target, in memory and
/// per engine like the loss windows (spec 2026-10-10 §4.2). The notes
/// (ResolverLying, ResolverFiltered, NameUnresolvable), DnsSummary and the
/// status lookupNames[] read it.
/// </summary>
public sealed class DnsResultLog
{
    public const int Keep = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Queue<DnsResult>> _results = new();

    public void Record(Guid targetId, PingResult result)
    {
        var (outcome, name) = Parse(result.Message);
        lock (_lock)
        {
            if (!_results.TryGetValue(targetId, out var queue))
                _results[targetId] = queue = new Queue<DnsResult>(Keep);
            queue.Enqueue(new DnsResult(result.Timestamp, result.Success, outcome, name, result.Message));
            while (queue.Count > Keep)
                queue.Dequeue();
        }
    }

    /// <summary>Oldest first; empty for a target with no DNS samples.</summary>
    public IReadOnlyList<DnsResult> Recent(Guid targetId)
    {
        lock (_lock)
            return _results.TryGetValue(targetId, out var queue) ? queue.ToList() : [];
    }

    /// <summary>"Filtered: NXDOMAIN (dns.google)" → ("Filtered", "dns.google").
    /// The name is the last parenthesised group; the outcome ends at the first
    /// space or colon.</summary>
    public static (string Outcome, string? Name) Parse(string message)
    {
        var text = message.Trim();
        string? name = null;
        if (text.EndsWith(')') && text.LastIndexOf(" (", StringComparison.Ordinal) is var open and >= 0)
        {
            name = text[(open + 2)..^1];
            text = text[..open];
        }
        var end = text.IndexOfAny([' ', ':']);
        var outcome = end < 0 ? text : text[..end];
        return (outcome, name is { Length: > 0 } ? name : null);
    }
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests` (the whole project: the transceiver, log and engine-pump tests all touch this).
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs Core/ConEngine/Dns/DnsResultLog.cs Tests/ConEngine.Tests/DnsQueryTransceiverTests.cs Tests/ConEngine.Tests/DnsResultLogTests.cs
git commit -m "feat(dns): a blocklisted seeded name is a Filtered hit (D11); every DNS result names what it asked"
```

---

### Task 2: Delete a managed resolver whose address is gone (D10)

**Files:**
- Modify: `Programs/ConWatch/Dns/SystemResolverReconciler.cs` (class doc, the retire loop)
- Modify: `Programs/ConWatch/Dns/SystemResolverService.cs` (class doc and the `ReconcileNowAsync` XML doc: "retired" now means "disabled because the watch is off")
- Test: `Tests/ConWatch.Tests/SystemResolverReconcilerTests.cs`, `Tests/ConWatch.Tests/SystemResolverServiceTests.cs`

**Interfaces:**
- Produces: `SystemResolverReconciler.Reconcile` removes a managed target whose address is absent from `found` (whatever its `Enabled`), logs `deleted {name}`, and never adds its address to `Retired`. The watch-off path is unchanged: present managed targets are disabled and retired. `Plan.Changed` is true on a deletion.

- [ ] **Step 1: Rewrite the affected reconciler tests**

Replace `A_vanished_address_is_disabled_and_remembered_never_deleted` with:

```csharp
    [Fact]
    public void A_vanished_address_deletes_the_managed_target_and_remembers_nothing()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([Router, managed], [], true, NoneRetired);

        Assert.True(plan.Changed);
        Assert.DoesNotContain(plan.Targets, t => t.SystemManaged);
        Assert.Contains(plan.Targets, t => t.Name == "Router");
        Assert.Empty(plan.Retired);
        Assert.Contains("deleted Your resolver 10.1.10.1", plan.Log);
    }

    [Fact]
    public void A_disabled_managed_target_whose_address_is_gone_is_deleted_too()
    {
        var retiredByUs = Managed("10.1.10.1", enabled: false);
        var byAdmin = Managed("10.1.10.2", enabled: false);
        var plan = SystemResolverReconciler.Reconcile([retiredByUs, byAdmin], [], true, new HashSet<string> { "10.1.10.1" });

        Assert.True(plan.Changed);
        Assert.Empty(plan.Targets);
        Assert.Empty(plan.Retired); // the entry goes with the target
    }

    [Fact]
    public void A_vanished_address_is_deleted_even_while_the_watch_is_off()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([managed], [], false, NoneRetired);
        Assert.Empty(plan.Targets);
        Assert.Empty(plan.Retired);
    }
```

Rewrite `A_returning_address_is_re_enabled_only_when_the_service_retired_it` so both targets are **present** (`[Found("10.1.10.1"), Found("10.1.10.2")]` is already the case; it stays valid) and `Addresses_match_case_insensitively_and_retired_entries_for_absent_addresses_are_kept` so the retired set's absent entry (`10.9.9.9`) is now **dropped**: rename it `Addresses_match_case_insensitively_and_a_retired_entry_with_no_target_is_dropped` and assert `Assert.Empty(plan.Retired)` after the re-enable. Keep `Turning_the_watch_off_retires_every_managed_target_and_on_restores_them` (both present) unchanged.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~SystemResolverReconcilerTests"`
Expected: FAIL (the vanished target is still present, disabled).

- [ ] **Step 3: Implement**

In `SystemResolverReconciler`, replace the retire loop with:

```csharp
        // Managed targets whose address is gone are deleted (D10: history rows
        // stay under the old id; a roaming laptop does not pile up resolvers).
        // Present ones are retired (disabled, remembered) only while the watch is off.
        for (var i = result.Count - 1; i >= 0; i--)
        {
            var t = result[i];
            if (!t.SystemManaged)
                continue;
            if (!foundByAddress.ContainsKey(t.Address))
            {
                result.RemoveAt(i);
                retiredNow.Remove(t.Address);
                log.Add($"deleted {t.Name}");
                changed = true;
                continue;
            }
            if (t.Enabled && !watchActive)
            {
                result[i] = t with { Enabled = false };
                retiredNow.Add(t.Address);
                log.Add($"retired {t.Name}");
                changed = true;
            }
        }
        // A retired entry with no managed target behind it is stale: drop it.
        if (retiredNow.RemoveWhere(a => !result.Any(t => t.SystemManaged && string.Equals(t.Address, a, StringComparison.OrdinalIgnoreCase))) > 0)
            changed = true;
```

Update the class doc: "deleted when it disappears (D10), disabled and remembered while the watch is off, re-enabled when the watch returns only if this reconciler disabled it". In `SystemResolverService`, update the class doc sentence and the `ReconcileNowAsync` doc ("retired" = disabled because the watch is off; a gone resolver is deleted and its history rows remain under its id).

- [ ] **Step 4: Update the host test**

In `SystemResolverServiceTests.A_vanished_resolver_is_retired_and_restored_when_it_returns`, rename to `A_vanished_resolver_is_deleted_and_recreated_when_it_returns`; after `Resolvers.Set("10.0.0.53")` assert with `Eventually` that `ManagedAsync("10.0.0.54")` is null, that `Dns.RetiredResolvers` does not contain `10.0.0.54`, then after `Set("10.0.0.53", "10.0.0.54")` that a managed target at `10.0.0.54` exists again with a **different** id than before (capture the first id).

- [ ] **Step 5: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~SystemResolver"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Programs/ConWatch/Dns/SystemResolverReconciler.cs Programs/ConWatch/Dns/SystemResolverService.cs Tests/ConWatch.Tests/SystemResolverReconcilerTests.cs Tests/ConWatch.Tests/SystemResolverServiceTests.cs
git commit -m "feat(dns): a managed resolver whose address is gone is deleted, not kept (D10)"
```

---

### Task 3: `DnsWatch` and `DnsSummary` in the engine

**Files:**
- Create: `Core/ConEngine/Dns/DnsWatch.cs`, `Core/ConEngine/Dns/DnsSummary.cs`
- Modify: `Core/ConEngine/Configuration/VerdictOptions.cs`, `Core/ConEngine/WatcherEngine.cs`, `Programs/ConWatch/WatcherHostService.cs`
- Test: `Tests/ConEngine.Tests/DnsSummaryTests.cs`

**Interfaces:**
- Produces:
  - `VerdictOptions.DnsWatch` (bool, default `true`): the watch is on and there is at least one lookup name. The host computes it as `Dns.Enabled != "false" && lookupNames.Current.Count > 0` in `StartEngineAsync`.
  - `ConEngine.Dns.DnsWatch(bool Enabled, IReadOnlyList<TargetMonitor> System, IReadOnlyList<TargetMonitor> Upstream)`; `WatcherEngine.DnsWatch` (built in `StartAsync` from the enabled Resolver-scope monitors, split by `Target.SystemManaged`; `Enabled = _verdictOptions.DnsWatch`).
  - `ConEngine.Dns.DnsSummaryState { Unknown, Ok, Degraded, Filtered, Down }`; `DnsSummary(bool Watched, DnsSummaryState State)`; `static DnsSummary DnsSummary.Compute(DnsWatch watch, DnsResultLog log)`; `static bool DnsSummary.IsFiltered(IReadOnlyList<DnsResult> recent)` (≥ 3 `Filtered` among the last 10 results that asked a seeded name — detected as outcome `Filtered`, `Answered` or `WrongAnswer`, i.e. the results that carry an expected-set judgement — and no `Answered` among them).

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/DnsSummaryTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Scheduling;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class DnsSummaryTests
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    private readonly DnsResultLog _log = new();

    private TargetMonitor Monitor(string name, string address, bool managed)
    {
        var target = new Target
        {
            Id = Guid.NewGuid(), Name = name, Address = address, ProfileName = "Fast",
            Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = managed,
        };
        var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
        monitor.StartTracking(IPAddress.Parse(address));
        return monitor;
    }

    private void Feed(TargetMonitor monitor, int count, bool success, string message = "Answered (dns.google)")
    {
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            var result = new PingResult(monitor.ResolvedAddress!, success, success ? 5 : 0, message, _time.GetUtcNow());
            monitor.RecordPingResult(result);
            _log.Record(monitor.Target.Id, result);
        }
    }

    [Fact]
    public void Off_or_no_system_resolvers_is_not_watched_or_unknown()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", managed: true);
        Assert.Equal(new DnsSummary(false, DnsSummaryState.Unknown),
            DnsSummary.Compute(new DnsWatch(false, [system], []), _log));
        Assert.Equal(new DnsSummary(false, DnsSummaryState.Unknown),
            DnsSummary.Compute(new DnsWatch(true, [], []), _log));
        Assert.Equal(new DnsSummary(true, DnsSummaryState.Unknown),
            DnsSummary.Compute(new DnsWatch(true, [system], []), _log)); // Starting: undetermined
    }

    [Fact]
    public void All_answering_is_Ok_some_down_is_Degraded_all_down_is_Down()
    {
        var a = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        var b = Monitor("Your resolver 10.0.0.2", "10.0.0.2", true);
        Feed(a, 10, true); Feed(b, 10, true);
        Assert.Equal(DnsSummaryState.Ok, DnsSummary.Compute(new DnsWatch(true, [a, b], []), _log).State);

        Feed(b, 20, false, "TimedOut (dns.google)");
        Assert.Equal(HostState.Down, b.CurrentState);
        Assert.Equal(DnsSummaryState.Degraded, DnsSummary.Compute(new DnsWatch(true, [a, b], []), _log).State);

        Feed(a, 20, false, "TimedOut (dns.google)");
        Assert.Equal(DnsSummaryState.Down, DnsSummary.Compute(new DnsWatch(true, [a, b], []), _log).State);
    }

    [Fact]
    public void A_faltering_resolver_is_Degraded_and_upstream_resolvers_never_change_the_state()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        var upstream = Monitor("1.1.1.1 directly", "1.1.1.1", false);
        Feed(system, 10, true);
        Feed(upstream, 20, false, "TimedOut (dns.google)");
        Assert.Equal(DnsSummaryState.Ok, DnsSummary.Compute(new DnsWatch(true, [system], [upstream]), _log).State);

        Feed(system, 4, false, "TimedOut (dns.google)"); // 6 of 10: Faltering
        Assert.Equal(HostState.Faltering, system.CurrentState);
        Assert.Equal(DnsSummaryState.Degraded, DnsSummary.Compute(new DnsWatch(true, [system], [upstream]), _log).State);
    }

    [Fact]
    public void A_resolver_that_blocks_the_seeded_names_is_Filtered()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        Feed(system, 10, true, "Filtered: 0.0.0.0 (dns.google)");
        Assert.Equal(HostState.Responding, system.CurrentState);
        Assert.Equal(DnsSummaryState.Filtered, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);

        // The blocked name answers for real again: not a block any more.
        Feed(system, 1, true, "Answered (dns.google)");
        Assert.Equal(DnsSummaryState.Ok, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);
    }

    [Fact]
    public void Hostname_target_answers_do_not_mask_a_block()
    {
        var system = Monitor("Your resolver 10.0.0.1", "10.0.0.1", true);
        for (var i = 0; i < 5; i++)
        {
            Feed(system, 1, true, "Filtered: NXDOMAIN (dns.google)");
            Feed(system, 1, true, "Answered (router.local)"); // an admin/hostname name: no expected set, no judgement
        }
        Assert.Equal(DnsSummaryState.Filtered, DnsSummary.Compute(new DnsWatch(true, [system], []), _log).State);
    }
}
```

The `IsFiltered` rule the tests pin (binding): over the last 10 *judged* results (outcome `Filtered`, `WrongAnswer` or `Answered`), at least 3 are `Filtered`, and every name that appears as `Filtered` in that window is still `Filtered` at its latest ask. A hostname-target name that answers (`Answered (router.local)`) never masks a block, because it is a different name; the block clears only when a blocked name answers for real.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsSummaryTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`Core/ConEngine/Dns/DnsWatch.cs`:

```csharp
using ConEngine.Tracking;

namespace ConEngine.Dns;

/// <summary>The name-resolution watch as the engine sees it (spec 2026-10-10
/// §4.4): whether it is on (enabled and there are lookup names), the enabled
/// system-managed resolver monitors, and the enabled upstream (admin-added)
/// resolver monitors.</summary>
public sealed record DnsWatch(bool Enabled, IReadOnlyList<TargetMonitor> System, IReadOnlyList<TargetMonitor> Upstream)
{
    public static DnsWatch Off { get; } = new(false, [], []);
}
```

`Core/ConEngine/Dns/DnsSummary.cs`:

```csharp
using ConEngine.Topology;
using ConEngine.Tracking;

namespace ConEngine.Dns;

public enum DnsSummaryState { Unknown, Ok, Degraded, Filtered, Down }

/// <summary>
/// The dns block's state (spec 2026-10-10 §4.4, §5.1), computed from the
/// system resolver monitors and the result log. One function, so status,
/// MCP, metrics and the notes never disagree. Upstream resolvers never
/// change it (they only decide the DnsDown blame).
/// </summary>
public sealed record DnsSummary(bool Watched, DnsSummaryState State)
{
    public const int FilteredWindow = 10;
    public const int FilteredRaise = 3;

    public static DnsSummary Compute(DnsWatch watch, DnsResultLog log)
    {
        if (!watch.Enabled || watch.System.Count == 0)
            return new DnsSummary(false, DnsSummaryState.Unknown);
        var states = watch.System.Select(m => m.CurrentState).ToList();
        var determined = states.Where(s => s is not (HostState.Initializing or HostState.Starting)).ToList();
        if (determined.Count == 0)
            return new DnsSummary(true, DnsSummaryState.Unknown);
        if (determined.All(s => s is HostState.Down or HostState.Unreachable))
            return new DnsSummary(true, DnsSummaryState.Down);
        if (determined.Any(s => s is HostState.Down or HostState.Unreachable or HostState.Faltering or HostState.Recovering))
            return new DnsSummary(true, DnsSummaryState.Degraded);
        var answering = watch.System.Where(m => TopologyCoordinator.Answering(m.CurrentState)).ToList();
        if (answering.Count > 0 && answering.All(m => IsFiltered(log.Recent(m.Target.Id))))
            return new DnsSummary(true, DnsSummaryState.Filtered);
        return new DnsSummary(true, DnsSummaryState.Ok);
    }

    /// <summary>Over the last <see cref="FilteredWindow"/> judged results (Filtered,
    /// WrongAnswer or Answered): at least <see cref="FilteredRaise"/> Filtered, and
    /// every name seen Filtered is still Filtered at its latest ask.</summary>
    public static bool IsFiltered(IReadOnlyList<DnsResult> recent)
    {
        var judged = recent.Where(r => r.Outcome is "Filtered" or "WrongAnswer" or "Answered").TakeLast(FilteredWindow).ToList();
        if (judged.Count(r => r.Outcome == "Filtered") < FilteredRaise)
            return false;
        var latestByName = judged.Where(r => r.Name is not null).GroupBy(r => r.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().Outcome, StringComparer.OrdinalIgnoreCase);
        var filteredNames = judged.Where(r => r.Outcome == "Filtered" && r.Name is not null).Select(r => r.Name!).Distinct(StringComparer.OrdinalIgnoreCase);
        return filteredNames.All(n => latestByName[n] == "Filtered");
    }
}
```

`VerdictOptions.cs`: add

```csharp
    /// <summary>The name-resolution watch is on and has lookup names (spec 2026-10-10).
    /// The host sets it from Dns.Enabled and the lookup list before each engine start.</summary>
    public bool DnsWatch { get; set; } = true;
```

`WatcherEngine.cs`: add a property `public ConEngine.Dns.DnsWatch DnsWatch { get; private set; } = ConEngine.Dns.DnsWatch.Off;` and, in `StartAsync` right after the monitors loop (before topology), build it:

```csharp
        // The name-resolution watch (spec 2026-10-10 §4.4): the enabled Resolver-scope
        // monitors, split into the host's own resolvers and the admin's upstream ones.
        var resolvers = _monitors.Values.Where(m => m.Target.Scope == TargetScope.Resolver).ToList();
        DnsWatch = new ConEngine.Dns.DnsWatch(_verdictOptions.DnsWatch,
            resolvers.Where(m => m.Target.SystemManaged).ToList(),
            resolvers.Where(m => !m.Target.SystemManaged).ToList());
```

`WatcherHostService.StartEngineAsync`: after the lookup-name refresh, `effectiveVerdict` gains `DnsWatch = dnsEnabled && lookupNames.Current.Count > 0,` (move the `dnsEnabled` local above if needed).

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsSummaryTests|FullyQualifiedName~WatcherEngineTests|FullyQualifiedName~ResolverScopeEngineTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/Dns Core/ConEngine/Configuration/VerdictOptions.cs Core/ConEngine/WatcherEngine.cs Programs/ConWatch/WatcherHostService.cs Tests/ConEngine.Tests/DnsSummaryTests.cs
git commit -m "feat(dns): DnsWatch on the engine and DnsSummary (Ok/Degraded/Filtered/Down/Unknown) shared by every reporter"
```

---

### Task 4: The `DnsDown` verdict

**Files:**
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs`, `Core/ConEngine/WatcherEngine.cs` (pass `DnsWatch`)
- Test: `Tests/ConEngine.Tests/VerdictDnsDownTests.cs`

**Interfaces:**
- Produces: `VerdictState.DnsDown` (after `LinkDegraded`, before `LinkDown` in the enum so ordinal order matches rank); `public enum DnsBlameKind { YourResolver, NoResolver }`; `public sealed record DnsBlame(DnsBlameKind Kind, string Message, IReadOnlyList<string> Resolvers)`; `VerdictSnapshot(VerdictState State, string? Detail, string? Operator, DnsBlame? Dns = null)`; constructor parameter `DnsWatch? dnsWatch = null` appended to `VerdictCoordinator(...)` (null = watch off, so every existing construction keeps compiling); `WatcherEngine` passes `DnsWatch`.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/VerdictDnsDownTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 §4.4: DnsDown fires only when the anchors prove the
/// internet reachable and every system resolver is Down.</summary>
public class VerdictDnsDownTests
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly List<TargetTransition> _transitions = new();
    private VerdictCoordinator _verdict = null!;

    public VerdictDnsDownTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
                new TargetConfig { Name = "Your resolver 192.168.1.1", Address = "192.168.1.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "Your resolver 192.168.1.2", Address = "192.168.1.2", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "9.9.9.9 directly", Address = "9.9.9.9", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance).Start();
    }

    private TargetMonitor M(string name) => _monitors[_graph.GetByName(name).Id];

    private void Start(bool watch = true, bool withUpstream = true)
    {
        var dns = new DnsWatch(watch,
            [M("Your resolver 192.168.1.1"), M("Your resolver 192.168.1.2")],
            withUpstream ? [M("9.9.9.9 directly")] : []);
        _verdict = new VerdictCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance, dns);
        _verdict.TransitionSource.Subscribe(_transitions.Add);
        _verdict.Start();
    }

    private void Feed(string name, int count, bool success)
    {
        var monitor = M(name);
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success, success ? 5 : 0,
                success ? "Answered (dns.google)" : "TimedOut (dns.google)", _time.GetUtcNow()));
        }
    }

    private void AnchorsUp() { Feed("Router", 10, true); Feed("Google", 10, true); Feed("Cloudflare", 10, true); }
    private void ResolversDown() { Feed("Your resolver 192.168.1.1", 20, false); Feed("Your resolver 192.168.1.2", 20, false); }

    [Fact]
    public void Anchors_up_and_every_system_resolver_down_is_DnsDown_blaming_your_resolver_when_the_upstream_answers()
    {
        Start();
        AnchorsUp();
        Feed("9.9.9.9 directly", 10, true);
        ResolversDown();

        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        var blame = _verdict.Current.Dns!;
        Assert.Equal(DnsBlameKind.YourResolver, blame.Kind);
        Assert.Equal("Your resolver 192.168.1.1 isn't answering, but 9.9.9.9 answers directly.", blame.Message);
        Assert.Equal(["192.168.1.1", "192.168.1.2"], blame.Resolvers);
        Assert.Contains(_transitions, t => t.To == HostState.Down && t.Reason.Verdict == "DnsDown");
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void With_no_upstream_answering_the_blame_is_NoResolver()
    {
        Start();
        AnchorsUp();
        Feed("9.9.9.9 directly", 20, false);
        ResolversDown();
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Assert.Equal(DnsBlameKind.NoResolver, _verdict.Current.Dns!.Kind);
        Assert.Equal("No resolver is answering, though the internet is reachable.", _verdict.Current.Dns.Message);
    }

    [Fact]
    public void With_no_upstream_configured_the_NoResolver_message_says_how_to_add_one()
    {
        Start(withUpstream: false);
        AnchorsUp();
        ResolversDown();
        Assert.EndsWith("Name a public resolver in Config → DNS so ConnectionWatcher can tell your resolver from everyone's.",
            _verdict.Current.Dns!.Message);
    }

    [Fact]
    public void One_system_resolver_still_answering_is_not_DnsDown()
    {
        Start();
        AnchorsUp();
        Feed("Your resolver 192.168.1.1", 10, true);
        Feed("Your resolver 192.168.1.2", 20, false);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.Null(_verdict.Current.Dns);
    }

    [Fact]
    public void Anchors_down_with_resolvers_down_is_LinkDown_with_the_dns_suffix_never_DnsDown()
    {
        Start();
        AnchorsUp();
        Feed("Google", 20, false); Feed("Cloudflare", 20, false);
        ResolversDown();
        Assert.Equal(VerdictState.LinkDown, _verdict.CurrentVerdict);
        Assert.EndsWith(" — name lookups are failing too", _verdict.CurrentDetail);
        Assert.DoesNotContain(_transitions, t => t.Reason.Verdict == "DnsDown");
    }

    [Fact]
    public void Undetermined_resolvers_wait_for_the_cap_then_conclude()
    {
        Start();
        AnchorsUp();
        Feed("Your resolver 192.168.1.1", 20, false); // the other stays Starting
        Assert.Equal(VerdictState.Unknown, _verdict.CurrentVerdict);
        Assert.Equal("Waiting for resolvers to report", _verdict.CurrentDetail);
        _time.Advance(VerdictCoordinator.AnchorWaitCap + TimeSpan.FromSeconds(1));
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
    }

    [Fact]
    public void A_watch_that_is_off_never_produces_DnsDown()
    {
        Start(watch: false);
        AnchorsUp();
        ResolversDown();
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
    }

    [Fact]
    public void Recovery_of_the_resolvers_returns_to_Healthy()
    {
        Start();
        AnchorsUp();
        ResolversDown();
        Assert.Equal(VerdictState.DnsDown, _verdict.CurrentVerdict);
        Feed("Your resolver 192.168.1.1", 20, true);
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict);
        Assert.Null(_verdict.Current.Dns);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~VerdictDnsDownTests"`
Expected: FAIL (compile: `DnsDown`, `DnsBlame`, the constructor parameter).

- [ ] **Step 3: Implement**

In `VerdictCoordinator.cs`:

- Enum: `Unknown, Healthy, SiteIncident, InternetWeather, LinkDegraded, DnsDown, LinkDown`.
- Add after the enum:

```csharp
public enum DnsBlameKind { YourResolver, NoResolver }

/// <summary>Why name lookups fail while the internet is reachable (spec 2026-10-10 §4.4).</summary>
public sealed record DnsBlame(DnsBlameKind Kind, string Message, IReadOnlyList<string> Resolvers);
```

- `VerdictSnapshot` becomes `public sealed record VerdictSnapshot(VerdictState State, string? Detail, string? Operator, DnsBlame? Dns = null);`.
- Field `private readonly ConEngine.Dns.DnsWatch _dns;` set from the new trailing constructor parameter `ConEngine.Dns.DnsWatch? dnsWatch = null` → `_dns = dnsWatch ?? ConEngine.Dns.DnsWatch.Off;`.
- `Recalculate`: `Classify` now returns a 4-tuple `(VerdictState, string, string? Operator, DnsBlame? Dns)`; the snapshot is built with the fourth element; the equal-state branch refreshes `Dns` too (`previous with { Detail = detail, Operator = operatorName, Dns = dns }`).
- In `Classify`, replace `return Quality(determined, states);` with:

```csharp
        if (counted.Any(a => Answering(a.State)))
        {
            // Reachable. Name resolution next (spec 2026-10-10 §4.4): it outranks quality.
            var dns = CheckDns(states);
            if (dns is { } down)
                return down;
            var (state, detail, op) = Quality(determined, states);
            return (state, detail, op, null);
        }
```

and add the LinkDown suffix where `Blame(VerdictState.LinkDown, states)` is returned:

```csharp
        var (down, downDetail, downOp) = Blame(VerdictState.LinkDown, states);
        return (down, AllSystemResolversDown(states) ? downDetail + " — name lookups are failing too" : downDetail, downOp, null);
```

- New members:

```csharp
    private bool AllSystemResolversDown(IReadOnlyDictionary<Guid, HostState> states) =>
        _dns.Enabled && _dns.System.Count > 0 &&
        _dns.System.All(m => states[m.Target.Id] is HostState.Down or HostState.Unreachable);

    /// <summary>DnsDown when every determined system resolver is Down (and none is still
    /// undetermined past the wait cap); null otherwise. Upstream resolvers decide the blame.</summary>
    private (VerdictState, string, string?, DnsBlame?)? CheckDns(IReadOnlyDictionary<Guid, HostState> states)
    {
        if (!_dns.Enabled || _dns.System.Count == 0)
            return null;
        var system = _dns.System.Select(m => (Monitor: m, State: states[m.Target.Id])).ToList();
        var determined = system.Where(s => !Undetermined(s.State)).ToList();
        if (determined.Count == 0)
            return null;
        if (!determined.All(s => s.State is HostState.Down or HostState.Unreachable))
            return null;
        if (system.Any(s => Undetermined(s.State)) && StillWaiting())
            return (VerdictState.Unknown, "Waiting for resolvers to report", null, null);

        var resolvers = system.Select(s => s.Monitor.Target.Address).ToList();
        var upstream = _dns.Upstream.FirstOrDefault(m => Answering(states[m.Target.Id]));
        DnsBlame blame = upstream is not null
            ? new DnsBlame(DnsBlameKind.YourResolver,
                $"Your resolver {resolvers[0]} isn't answering, but {upstream.Target.Address} answers directly.", resolvers)
            : new DnsBlame(DnsBlameKind.NoResolver,
                "No resolver is answering, though the internet is reachable." +
                (_dns.Upstream.Count == 0 ? " Name a public resolver in Config → DNS so ConnectionWatcher can tell your resolver from everyone's." : ""),
                resolvers);
        return (VerdictState.DnsDown, $"DnsDown: {blame.Message}", null, blame);
    }
```

- `Map`: add `VerdictState.DnsDown => HostState.Down,` (put it with `LinkDown`).
- `StillWaiting` is reused as is: the DNS wait only runs when a counted anchor answers, so the anchor wait cannot be active at the same time; `_waitingThisPass` handles both.

`WatcherEngine.StartAsync`: pass `DnsWatch` as the new last argument when constructing `VerdictCoordinator` (the engine's `DnsWatch` is built before the verdict, Task 3).

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: PASS (existing verdict tests construct the coordinator without the parameter and are unaffected; `MetricsEndpoint` enumerates `VerdictState` values, so its one-hot series gains a `DnsDown` line — check `Tests/ConWatch.Tests/MetricsEndpointTests.cs` and add the state to any pinned list).

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/Topology/VerdictCoordinator.cs Core/ConEngine/WatcherEngine.cs Tests/ConEngine.Tests/VerdictDnsDownTests.cs Tests/ConWatch.Tests/MetricsEndpointTests.cs
git commit -m "feat(verdict): DnsDown — anchors answer but every system resolver is Down; blame names your resolver or no resolver"
```

---

### Task 5: The five resolver notes

**Files:**
- Modify: `Core/ConEngine/Topology/ModelCheckCoordinator.cs`, `Core/ConEngine/WatcherEngine.cs` (pass `DnsWatch` and `DnsResults`), `Libraries/ConInfrastructure/Tray/TrayStatus.cs` (`NoteLabel`), `Programs/ConWatch/ClientApp/src/lib/types.ts` (`ModelWarning.kind`)
- Test: `Tests/ConEngine.Tests/ResolverNotesTests.cs`, `Tests/ConWatch.Tests/TrayStatusTests.cs`

**Interfaces:**
- Produces: `ModelWarningKind` gains `ResolverPartial, ResolverLying, ResolverFiltered, UpstreamResolverDown, NameUnresolvable`; `ModelCondition.KeyFor` maps them to `resolver-partial`, `resolver-lying`, `resolver-filtered`, `upstream-resolver-down`, `name-unresolvable`; `ModelCheckCoordinator` constructor gains trailing `ConEngine.Dns.DnsWatch? dnsWatch = null, ConEngine.Dns.DnsResultLog? dnsResults = null`. `TrayStatus.NoteLabel` returns a label for each kind.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/ResolverNotesTests.cs` (same fixture style as `DnsSummaryTests`: build Resolver monitors by hand, a `DnsResultLog`, and a `ModelCheckCoordinator` over a graph holding only the resolvers plus one anchor so `_beyond` is empty for them):

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 §4.5: the resolver notes. Admin-facing; never the verdict.</summary>
public class ResolverNotesTests : IDisposable
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly DnsResultLog _log = new();
    private readonly List<ModelConditionChange> _changes = new();
    private ModelCheckCoordinator _check = null!;

    public ResolverNotesTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Google", Address = "8.8.8.8" },
                new TargetConfig { Name = "Your resolver 10.0.0.1", Address = "10.0.0.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "Your resolver 10.0.0.2", Address = "10.0.0.2", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true },
                new TargetConfig { Name = "1.1.1.1 directly", Address = "1.1.1.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver },
            ],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
    }

    private TargetMonitor M(string name) => _monitors[_graph.GetByName(name).Id];

    private void Start()
    {
        var dns = new DnsWatch(true, [M("Your resolver 10.0.0.1"), M("Your resolver 10.0.0.2")], [M("1.1.1.1 directly")]);
        _check = new ModelCheckCoordinator(_graph, _monitors, false, _time, NullLogger<ModelCheckCoordinator>.Instance,
            null, dns, _log);
        _check.ChangeSource.Subscribe(_changes.Add);
        _check.Start();
    }

    private void Feed(string name, int count, bool success, string message)
    {
        var monitor = M(name);
        for (var i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            var result = new PingResult(monitor.ResolvedAddress!, success, success ? 5 : 0, message, _time.GetUtcNow());
            monitor.RecordPingResult(result);
            _log.Record(monitor.Target.Id, result);
        }
        _check.Evaluate();
    }

    private void AllAnswer()
    {
        Feed("Your resolver 10.0.0.1", 10, true, "Answered (dns.google)");
        Feed("Your resolver 10.0.0.2", 10, true, "Answered (dns.google)");
        Feed("1.1.1.1 directly", 10, true, "Answered (dns.google)");
    }

    private bool Active(ModelWarningKind kind, string? target = null) =>
        _check.Active.Any(c => c.Kind == kind && (target is null || c.TargetName == target));

    [Fact]
    public void One_of_two_system_resolvers_down_is_ResolverPartial()
    {
        Start(); AllAnswer();
        Feed("Your resolver 10.0.0.2", 20, false, "TimedOut (dns.google)");
        Assert.True(Active(ModelWarningKind.ResolverPartial, "Your resolver 10.0.0.2"));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ResolverPartial &&
            c.Message == "Your secondary resolver 10.0.0.2 isn't answering; 10.0.0.1 is.");
        Feed("Your resolver 10.0.0.2", 20, true, "Answered (dns.google)");
        Assert.False(Active(ModelWarningKind.ResolverPartial));
    }

    [Fact]
    public void Wrong_answers_for_a_seeded_name_are_ResolverLying()
    {
        Start(); AllAnswer();
        Feed("Your resolver 10.0.0.1", 3, false, "WrongAnswer: 10.99.0.1 (dns.google)");
        Assert.True(Active(ModelWarningKind.ResolverLying, "Your resolver 10.0.0.1"));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ResolverLying &&
            c.Message == "Your resolver 10.0.0.1 answered dns.google with 10.99.0.1 — a captive portal or interception?");
    }

    [Fact]
    public void Blocked_seeded_names_are_ResolverFiltered_not_lying()
    {
        Start(); AllAnswer();
        Feed("Your resolver 10.0.0.1", 3, true, "Filtered: 0.0.0.0 (dns.google)");
        Assert.True(Active(ModelWarningKind.ResolverFiltered, "Your resolver 10.0.0.1"));
        Assert.False(Active(ModelWarningKind.ResolverLying));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ResolverFiltered &&
            c.Message == "Your resolver 10.0.0.1 blocks dns.google (answered 0.0.0.0) — a Pi-hole or AdGuard blocklist? " +
                         "Name resolution still works; the seeded lookup names can't prove it. Add a lookup name your resolver allows.");
    }

    [Fact]
    public void An_upstream_resolver_down_while_a_system_one_answers_is_UpstreamResolverDown()
    {
        Start(); AllAnswer();
        Feed("1.1.1.1 directly", 20, false, "TimedOut (dns.google)");
        Assert.True(Active(ModelWarningKind.UpstreamResolverDown, "1.1.1.1 directly"));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.UpstreamResolverDown &&
            c.Message == "1.1.1.1 isn't answering direct queries; your resolver is fine.");
        Assert.False(Active(ModelWarningKind.ResolverPartial));
    }

    [Fact]
    public void A_name_that_misses_everywhere_while_others_hit_is_NameUnresolvable()
    {
        Start(); AllAnswer();
        foreach (var r in new[] { "Your resolver 10.0.0.1", "Your resolver 10.0.0.2", "1.1.1.1 directly" })
            for (var i = 0; i < 3; i++)
            {
                Feed(r, 1, false, "ServFail (example.org)");
                Feed(r, 1, true, "Answered (dns.google)");
            }
        Assert.True(Active(ModelWarningKind.NameUnresolvable));
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.NameUnresolvable && c.TargetId is null &&
            c.Message == "example.org isn't resolving anywhere.");
    }

    [Fact]
    public void Keys_are_stable_per_kind_and_target()
    {
        var id = Guid.NewGuid();
        Assert.Equal($"resolver-partial:{id:D}", ModelCondition.KeyFor(ModelWarningKind.ResolverPartial, id));
        Assert.Equal($"resolver-lying:{id:D}", ModelCondition.KeyFor(ModelWarningKind.ResolverLying, id));
        Assert.Equal($"resolver-filtered:{id:D}", ModelCondition.KeyFor(ModelWarningKind.ResolverFiltered, id));
        Assert.Equal($"upstream-resolver-down:{id:D}", ModelCondition.KeyFor(ModelWarningKind.UpstreamResolverDown, id));
        Assert.Equal("name-unresolvable", ModelCondition.KeyFor(ModelWarningKind.NameUnresolvable, null));
    }

    public void Dispose() => _check?.Dispose();
}
```

Append to `Tests/ConWatch.Tests/TrayStatusTests.cs` a theory that `TrayStatus.NoteLabel` returns, for `{"kind":"ResolverPartial","targetName":"Your resolver 10.0.0.2"}` → `"Your resolver 10.0.0.2 isn't answering (another resolver is)"`, `ResolverLying` → `"Your resolver 10.0.0.1 answers with the wrong addresses"`, `ResolverFiltered` → `"Your resolver 10.0.0.1 blocks the lookup names"`, `UpstreamResolverDown` → `"1.1.1.1 directly isn't answering"`, `NameUnresolvable` (no target) → `"A lookup name isn't resolving anywhere"` (follow the file's existing pattern for building the `JsonElement`).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ResolverNotesTests"` and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~TrayStatusTests"`
Expected: FAIL (compile / default label).

- [ ] **Step 3: Implement**

`ModelCheckCoordinator.cs`:

- Enum: append the five kinds with one-line doc comments.
- `KeyFor`: add the five mappings listed in Interfaces.
- Fields `private readonly ConEngine.Dns.DnsWatch _dns; private readonly ConEngine.Dns.DnsResultLog? _dnsResults;` from two new trailing constructor parameters `ConEngine.Dns.DnsWatch? dnsWatch = null, ConEngine.Dns.DnsResultLog? dnsResults = null` (`_dns = dnsWatch ?? DnsWatch.Off`).
- In `Evaluate`, after the `_beyond` loop and before the diff against `_active`, call `CheckResolvers(found, states)`:

```csharp
    public const int ResolverWindow = 10;
    public const int ResolverRaise = 3;
    public const int NameMisses = 3;

    private void CheckResolvers(Dictionary<string, ModelCondition> found, IReadOnlyDictionary<Guid, HostState> states)
    {
        if (!_dns.Enabled || _dnsResults is null || _dns.System.Count == 0)
            return;
        var system = _dns.System.Select(m => (Monitor: m, State: states[m.Target.Id])).ToList();
        var answering = system.Where(s => TopologyCoordinator.Answering(s.State)).ToList();
        var down = system.Where(s => s.State is HostState.Down or HostState.Unreachable).ToList();

        // ResolverPartial: some, not all, system resolvers Down.
        if (answering.Count > 0 && down.Count > 0)
            foreach (var (monitor, _) in down)
                Add(found, ModelWarningKind.ResolverPartial, monitor.Target,
                    $"Your secondary resolver {monitor.Target.Address} isn't answering; {answering[0].Monitor.Target.Address} is.");

        // UpstreamResolverDown: a system resolver answers but an upstream one is Down.
        if (answering.Count > 0)
            foreach (var upstream in _dns.Upstream.Where(m => states[m.Target.Id] is HostState.Down or HostState.Unreachable))
                Add(found, ModelWarningKind.UpstreamResolverDown, upstream.Target,
                    $"{upstream.Target.Address} isn't answering direct queries; your resolver is fine.");

        // ResolverLying / ResolverFiltered, per resolver, from the result log.
        foreach (var monitor in _dns.System.Concat(_dns.Upstream))
        {
            var recent = _dnsResults.Recent(monitor.Target.Id);
            var judged = recent.Where(r => r.Outcome is "Filtered" or "WrongAnswer" or "Answered").TakeLast(ResolverWindow).ToList();
            var lies = judged.Where(r => r.Outcome == "WrongAnswer").ToList();
            if (lies.Count >= ResolverRaise)
            {
                var last = lies[^1];
                var detail = last.Message[(last.Message.IndexOf(':') + 1)..].Trim();
                detail = detail[..detail.LastIndexOf(" (", StringComparison.Ordinal)];
                Add(found, ModelWarningKind.ResolverLying, monitor.Target,
                    $"{monitor.Target.Name} answered {last.Name} with {detail} — a captive portal or interception?");
            }
            else if (ConEngine.Dns.DnsSummary.IsFiltered(recent))
            {
                var last = judged.Last(r => r.Outcome == "Filtered");
                var detail = last.Message[(last.Message.IndexOf(':') + 1)..].Trim();
                detail = detail[..detail.LastIndexOf(" (", StringComparison.Ordinal)];
                Add(found, ModelWarningKind.ResolverFiltered, monitor.Target,
                    $"{monitor.Target.Name} blocks {last.Name} (answered {detail}) — a Pi-hole or AdGuard blocklist? " +
                    "Name resolution still works; the seeded lookup names can't prove it. Add a lookup name your resolver allows.");
            }
        }

        // NameUnresolvable: one name missed its last NameMisses asks on every answering
        // resolver that asked it, while some other name hit on each of them.
        var answeringResolvers = _dns.System.Concat(_dns.Upstream)
            .Where(m => TopologyCoordinator.Answering(states[m.Target.Id])).ToList();
        if (answeringResolvers.Count == 0)
            return;
        var byName = new Dictionary<string, (int Resolvers, int Failing)>(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in answeringResolvers)
        {
            var recent = _dnsResults.Recent(monitor.Target.Id);
            if (!recent.Any(r => r.Success && r.Name is not null))
                continue; // nothing hit here: no basis to single out a name
            foreach (var group in recent.Where(r => r.Name is not null).GroupBy(r => r.Name!, StringComparer.OrdinalIgnoreCase))
            {
                var lastAsks = group.TakeLast(NameMisses).ToList();
                var failing = lastAsks.Count == NameMisses && lastAsks.All(r => !r.Success);
                var entry = byName.GetValueOrDefault(group.Key);
                byName[group.Key] = (entry.Resolvers + 1, entry.Failing + (failing ? 1 : 0));
            }
        }
        foreach (var (name, (resolvers, failing)) in byName)
            if (resolvers == answeringResolvers.Count && failing == resolvers)
            {
                Add(found, ModelWarningKind.NameUnresolvable, null, $"{name} isn't resolving anywhere.");
                break; // one note; the key has no target
            }
    }
```

(`Add(found, kind, null, message)` already yields the target-less key.) Resolvers have no descendants, so the existing `_beyond` loop never touches them; nothing else changes.

`WatcherEngine.StartAsync`: pass `DnsWatch, DnsResults` as the two new trailing arguments to `ModelCheckCoordinator` (after `Loss`).

`TrayStatus.NoteLabel`: add the five arms with the labels in Step 1.

`types.ts`: `kind: "HopBypassed" | "NeverAnswered" | "UnprovableUp" | "ProbeOnlyLoss" | "ResolverPartial" | "ResolverLying" | "ResolverFiltered" | "UpstreamResolverDown" | "NameUnresolvable" | string;`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ResolverNotesTests|FullyQualifiedName~ModelCheckCoordinatorTests"`, `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~TrayStatusTests"`, `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~ModelWarningLedgerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/Topology/ModelCheckCoordinator.cs Core/ConEngine/WatcherEngine.cs Libraries/ConInfrastructure/Tray/TrayStatus.cs Programs/ConWatch/ClientApp/src/lib/types.ts Tests/ConEngine.Tests/ResolverNotesTests.cs Tests/ConWatch.Tests/TrayStatusTests.cs
git commit -m "feat(notes): ResolverPartial, ResolverLying, ResolverFiltered, UpstreamResolverDown, NameUnresolvable"
```

---

### Task 6: The `dns` block in status and MCP, the metrics gauge, two health codes

**Files:**
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`, `Programs/ConWatch/Api/MetricsEndpoint.cs`, `Libraries/ConPersistence/Config/ConfigHealth.cs`
- Test: `Tests/ConWatch.Tests/StatusShapeTests.cs`, `Tests/ConWatch.Tests/McpEndpointTests.cs`, `Tests/ConWatch.Tests/MetricsEndpointTests.cs`, `Tests/ConPersistence.Tests/ConfigHealthTests.cs`, new `Tests/ConWatch.Tests/DnsStatusTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record StatusResolver(Guid Id, string Address, string Label, string State, string? Detail, bool Enabled);
  public sealed record StatusDnsBlame(string Kind, string Message, IReadOnlyList<string> Resolvers);
  public sealed record StatusLookupName(string Name, string LastResult, string? Detail); // LastResult: "ok" | "miss" | "filtered" | "none"
  public sealed record StatusDns(bool Watched, string State, IReadOnlyList<StatusResolver> SystemResolvers,
      IReadOnlyList<StatusResolver> UpstreamResolvers, StatusDnsBlame? Blame, IReadOnlyList<StatusLookupName> LookupNames);
  ```
  `StatusSnapshot(..., IReadOnlyList<StatusWarning> Warnings, StatusDns? Dns)`; `StatusSnapshotBuilder.Build(WatcherEngine?, ModelWarningLedger, IReadOnlyList<DnsLookupName> lookupNames)`; `StatusSnapshot.Disabled` has `Dns = null`. MCP `get_current_status` gains `dns`. Metrics `conwatch_dns_up` (1 for Ok/Degraded/Filtered, 0 for Down, absent when not watched or Unknown). Health codes `no-upstream-resolver`, `dns-not-watched`.

- [ ] **Step 1: Write the failing tests**

Update `StatusShapeTests`: the top-level key list becomes `"dns", "enabled", "pools", "targets", "verdict", "warnings"` (sorted as the test sorts them) and the disabled JSON pin becomes `{"enabled":false,"verdict":null,"targets":[],"pools":[],"warnings":[],"dns":null}`. Create `Tests/ConWatch.Tests/DnsStatusTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using ConWatch.NameResolution;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class DnsStatusTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;
    public DnsStatusTests() => _client = _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    /// <summary>One stateless MCP tools/call, the way McpEndpointTests does it; returns the tool's JSON text parsed.</summary>
    private async Task<JsonElement> CallToolAsync(string name)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name, arguments = new { } },
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            body = body.Split('\n').First(l => l.StartsWith("data:"))["data:".Length..].Trim();
        var text = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    [Fact]
    public async Task Status_and_MCP_carry_the_dns_block_and_metrics_carry_the_gauge()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);

        JsonElement dns = default;
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetFromJsonAsync<JsonElement>("/api/v1/status");
            if (!status.TryGetProperty("dns", out dns) || dns.ValueKind != JsonValueKind.Object) return false;
            return dns.GetProperty("systemResolvers").GetArrayLength() == 1;
        });
        Assert.True(dns.GetProperty("watched").GetBoolean());
        Assert.Contains(dns.GetProperty("state").GetString(), new[] { "Unknown", "Ok" }); // the fake answers every ping
        var resolver = dns.GetProperty("systemResolvers")[0];
        Assert.Equal("10.0.0.53", resolver.GetProperty("address").GetString());
        Assert.Equal("Your resolver 10.0.0.53", resolver.GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, dns.GetProperty("blame").ValueKind);
        Assert.Equal(4, dns.GetProperty("lookupNames").GetArrayLength());
        Assert.Equal("dns.google", dns.GetProperty("lookupNames")[0].GetProperty("name").GetString());

        var mcp = await CallToolAsync("get_current_status");
        Assert.True(mcp.TryGetProperty("dns", out var mcpDns) && mcpDns.ValueKind == JsonValueKind.Object);

        var metrics = await _client.GetStringAsync("/metrics");
        Assert.Contains("conwatch_dns_up ", metrics);
        Assert.Contains("conwatch_verdict{state=\"DnsDown\"} 0", metrics);
    }
}
```

Append to `ConfigHealthTests.cs` (the class seeds `ConfigStoreTests.ValidHome()` with anchor pool "Internet" in `InitializeAsync`, and has `_health`, `_settings`, `_service` and a `Codes()` helper returning the warning codes):

```csharp
    [Fact]
    public async Task No_upstream_resolver_and_a_disabled_watch_are_health_warnings()
    {
        // ValidHome has no Resolver targets at all: nothing is watched yet (no system resolver found).
        Assert.Contains("dns-not-watched", await Codes());

        // A system resolver and the seeded names: watched, but no upstream resolver is named.
        await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto
            {
                Name = "Your resolver 192.168.1.1", Address = "192.168.1.1", Profile = "Resolver",
                Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true,
            });
            return ConfigResult.Ok();
        });
        await _settings.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.DnsLookupNames] = DnsLookupName.ToJson(DnsLookupName.Seeded),
        });
        var codes = await Codes();
        Assert.Contains("no-upstream-resolver", codes);
        Assert.DoesNotContain("dns-not-watched", codes);

        // An upstream resolver silences that; turning the watch off replaces it with dns-not-watched.
        await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "1.1.1.1 directly", Address = "1.1.1.1", Profile = "Resolver", Probe = "dns", Scope = TargetScope.Resolver });
            return ConfigResult.Ok();
        });
        Assert.DoesNotContain("no-upstream-resolver", await Codes());

        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.DnsEnabled] = "false" });
        codes = await Codes();
        Assert.Contains("dns-not-watched", codes);
        Assert.DoesNotContain("no-upstream-resolver", codes);
    }
```

(`using ConCore.Dns; using ConCore.Targets; using ConPersistence.Config;` as needed. `TestDbFactory` builds the schema with `EnsureCreated`, so no `Dns.*` settings are seeded there: the test writes the names itself.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsStatusTests|FullyQualifiedName~StatusShapeTests"` and `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~ConfigHealthTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

`StatusSnapshotBuilder.cs`: add the four records above, extend `StatusSnapshot` with `StatusDns? Dns`, `Disabled` with `null`, and in `Build` (new third parameter `IReadOnlyList<ConCore.Dns.DnsLookupName> lookupNames`):

```csharp
        var summary = ConEngine.Dns.DnsSummary.Compute(engine.DnsWatch, engine.DnsResults);
        StatusResolver Resolver(TargetMonitor m) => new(m.Target.Id, m.Target.Address, m.Target.Name,
            m.CurrentState.ToString(),
            engine.DnsResults.Recent(m.Target.Id) is { Count: > 0 } r && !TopologyCoordinator.Answering(m.CurrentState) ? r[^1].Message : null,
            m.Target.Enabled);
        var blame = engine.Verdict?.Current.Dns is { } b ? new StatusDnsBlame(b.Kind.ToString(), b.Message, b.Resolvers) : null;
        var allRecent = engine.DnsWatch.System.Concat(engine.DnsWatch.Upstream)
            .SelectMany(m => engine.DnsResults.Recent(m.Target.Id)).OrderBy(r => r.At).ToList();
        var names = lookupNames.Select(n =>
        {
            var last = allRecent.LastOrDefault(r => string.Equals(r.Name, n.Name, StringComparison.OrdinalIgnoreCase));
            var result = last is null ? "none" : last.Outcome == "Filtered" ? "filtered" : last.Success ? "ok" : "miss";
            return new StatusLookupName(n.Name, result, last?.Message);
        }).ToList();
        var dns = new StatusDns(summary.Watched, summary.State.ToString(),
            engine.DnsWatch.System.Select(Resolver).ToList(), engine.DnsWatch.Upstream.Select(Resolver).ToList(), blame, names);
```

and pass `dns` as the snapshot's last argument. Every caller of `Build` (`StatusEndpoints`, `StatusBroadcaster`, `ConWatchMcpTools`, the dashboard's first paint in `Index.cshtml.cs`) passes `lookupNames.Current` from the `DnsLookupNames` singleton (inject it where missing). MCP: add `dns = snapshot?.Dns,` and extend the tool description with one sentence: "`dns` is the name-resolution watch: watched, state (Ok, Degraded, Filtered = the resolver blocks the seeded names but works, Down, Unknown), the host's resolvers and any upstream ones, the blame when the verdict is DnsDown, and each lookup name's last result."

`MetricsEndpoint`: after the verdict one-hot block,

```csharp
                var dns = ConEngine.Dns.DnsSummary.Compute(engine.DnsWatch, engine.DnsResults);
                if (dns.Watched && dns.State != ConEngine.Dns.DnsSummaryState.Unknown)
                {
                    Family(sb, "conwatch_dns_up", "gauge", "1 while the host's name resolution works (Ok, Degraded or Filtered), 0 when every system resolver is down.");
                    sb.Append("conwatch_dns_up ").Append(dns.State == ConEngine.Dns.DnsSummaryState.Down ? 0 : 1).Append('\n');
                }
```

`ConfigHealth.EvaluateAsync`: after the anchor checks (before `return warnings`), add:

```csharp
        // Name-resolution watch (spec 2026-10-10 §4.5).
        var dnsEnabled = await settings.GetAsync(SettingKeys.DnsEnabled) != "false";
        var names = ConCore.Dns.DnsLookupName.FromJson(await settings.GetAsync(SettingKeys.DnsLookupNames));
        var addresses = await db.Targets.Where(t => t.Enabled && t.Scope != TargetScope.Resolver).Select(t => t.Address).ToListAsync();
        var hasHostnameTarget = addresses.Any(a => !System.Net.IPAddress.TryParse(a, out _));
        var systemResolvers = await db.Targets.CountAsync(t => t.Scope == TargetScope.Resolver && t.SystemManaged);
        if (!dnsEnabled || (names.Count == 0 && !hasHostnameTarget) || systemResolvers == 0)
            warnings.Add(new HealthWarning("dns-not-watched",
                !dnsEnabled ? "Name resolution isn't being watched: the DNS watch is turned off (Dns.Enabled)."
                : systemResolvers == 0 ? "Name resolution isn't being watched: no resolver was found in this machine's network settings."
                : "Name resolution isn't being watched: the lookup-name list is empty and no hostname target is configured."));
        else if (!await db.Targets.AnyAsync(t => t.Scope == TargetScope.Resolver && !t.SystemManaged && t.Enabled))
            warnings.Add(new HealthWarning("no-upstream-resolver",
                "Name a public resolver (1.1.1.1, 8.8.8.8, …) as an upstream Resolver target so ConnectionWatcher can tell your resolver from everyone's."));
```

Note the early `return warnings` branches for a missing anchor pool: move the DNS block above them into a helper `AddDnsWarningsAsync(db, warnings)` called before those returns so a node without an anchor pool still gets DNS health.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests` and `dotnet test Tests/ConPersistence.Tests`
Expected: PASS (fix any other status-key pin the run exposes, and note it in the report).

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch Libraries/ConPersistence/Config/ConfigHealth.cs Tests/ConWatch.Tests Tests/ConPersistence.Tests/ConfigHealthTests.cs
git commit -m "feat(api): dns block in status and MCP, conwatch_dns_up gauge, dns-not-watched and no-upstream-resolver health codes"
```

---

### Task 7: ISP report line and history scope

**Files:**
- Modify: `Libraries/ConPersistence/Report/IspReportBuilder.cs`, `Programs/ConWatch/Pages/Report.cshtml`, `Libraries/ConPersistence/Queries/TransitionDto.cs`, `Libraries/ConPersistence/Queries/HistoryQueries.cs`
- Test: `Tests/ConPersistence.Tests/IspReportBuilderTests.cs`, `Tests/ConPersistence.Tests/TransitionDtoTests.cs`, `Tests/ConPersistence.Tests/HistoryScopeTests.cs`

**Interfaces:**
- Produces: `IspReport.DnsDownTime` (new named parameter with default `default(TimeSpan)`, appended after `OperatorName`); `BuildEpisodesAsync` totals `DnsDown` separately, never adds it to `AttributedKinds` or the episodes; `Report.cshtml` shows `<li><b>Name-resolution failures (your resolver, not the connection):</b> …</li>`. `TransitionDto.From(TransitionRow, ISet<Guid> localIds, ISet<Guid> resolverIds)` tags Resolver-scope subjects; `HistoryQueries` `ScopeFilter.Internet` keeps Resolver rows on the internet side (unchanged) and passes the resolver ids through for tagging.

- [ ] **Step 1: Write the failing tests**

Append to `IspReportBuilderTests.cs` (it has `_builder` and a helper `AddVerdict(DateTime at, string verdict, string? detail = null)`):

```csharp
    [Fact]
    public async Task DnsDown_time_is_its_own_figure_and_never_attributed_to_the_ISP()
    {
        var t0 = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        await AddVerdict(t0, "Healthy");
        await AddVerdict(t0.AddMinutes(10), "DnsDown");
        await AddVerdict(t0.AddMinutes(25), "Healthy");
        await AddVerdict(t0.AddMinutes(40), "LinkDown");
        await AddVerdict(t0.AddMinutes(45), "Healthy");

        var report = await _builder.BuildAsync(t0, t0.AddHours(1));

        Assert.Equal(TimeSpan.FromMinutes(15), report.DnsDownTime);
        Assert.Equal(TimeSpan.FromMinutes(5), report.LinkDownTime);
        Assert.DoesNotContain(report.Episodes, e => e.Kind == "DnsDown");
    }
```

Append to `TransitionDtoTests.cs` a case that a row whose subject id is in `resolverIds` reads as `TargetScope.Resolver`, and to `HistoryScopeTests.cs` a case that a Resolver target's transitions appear under `ScopeFilter.Internet` with `scope == "Resolver"` and not under `ScopeFilter.Local`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~IspReportBuilderTests|FullyQualifiedName~TransitionDtoTests|FullyQualifiedName~HistoryScopeTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

`IspReportBuilder`: in `BuildEpisodesAsync`'s `CloseSegment`, add a second branch: `else if (end > cursor && state == "DnsDown") totals["DnsDown"] = totals.GetValueOrDefault("DnsDown") + (end - cursor);` (no episode). `BuildAsync` passes `DnsDownTime: totals.GetValueOrDefault("DnsDown")`. `IspReport` gains `TimeSpan DnsDownTime = default` after `OperatorName`. `Report.cshtml`: add the line after "Widespread internet disturbances". `TransitionDto.From`: new overload with `resolverIds`, the two-set version sets `Scope = localIds.Contains(id) ? Local : resolverIds.Contains(id) ? Resolver : Verdict`; keep the old overload delegating with an empty set. `HistoryQueries`: where `localIds` is computed, also compute `resolverIds` (`Scope == TargetScope.Resolver`) and pass both to `From`; the `Internet` filter stays "not Local".

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConPersistence.Tests` and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~HistoryApiTests|FullyQualifiedName~HistoryScopeApiTests|FullyQualifiedName~ReportPageTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConPersistence Programs/ConWatch/Pages/Report.cshtml Tests/ConPersistence.Tests
git commit -m "feat(report): DnsDown time is its own line, never the ISP's; history tags Resolver-scope transitions"
```

---

### Task 8: The three consumer one-liners and the README section

**Files:**
- Modify: `Programs/ConWatch/ClientApp/src/components/VerdictBanner.tsx` (`slabFor`), `Libraries/ConInfrastructure/Tray/TrayStatus.cs` (verdict mapping), `Libraries/ConNotify/Builders.cs` (`InternetPayload`), `README.md`
- Test: `Programs/ConWatch/ClientApp/src/components/VerdictBanner.test.ts`, `Tests/ConWatch.Tests/TrayStatusTests.cs`, `Tests/ConNotify.Tests` (the `HaDiscovery`/payload tests)

Why here and not Plan 4: a verdict value that the banner renders as "Checking…", the tray shows green and MQTT reports ON would mislead edge users for the weeks between plans. Plan 4 keeps the `dns` binary sensor, toasts, webhook `dns` block and MQTT attributes.

- [ ] **Step 1: Write the failing tests**

`VerdictBanner.test.ts`: add

```ts
it("DnsDown is a red 'up but name lookups fail' slab carrying the blame", () => {
  const slab = slabFor({ state: "DnsDown", detail: "DnsDown: Your resolver 10.0.0.1 isn't answering, but 1.1.1.1 answers directly.", operator: null });
  expect(slab.cls).toBe("linkdown");
  expect(slab.icon).toBe("down");
  expect(slab.headline).toBe("Your internet is UP, but name lookups are failing");
  expect(slab.caveat).toBe("Your resolver 10.0.0.1 isn't answering, but 1.1.1.1 answers directly.");
});
```

`TrayStatusTests.cs`: `"DnsDown"` maps to `TrayState.Down`. ConNotify tests: `HaDiscovery.InternetPayload("DnsDown") == "OFF"` and `InternetPayload("LinkDegraded") == "ON"` (keep).

- [ ] **Step 2: Run to verify they fail**

Run: `npm --prefix Programs/ConWatch/ClientApp test -- --run`, `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~TrayStatusTests"`, `dotnet test Tests/ConNotify.Tests`
Expected: FAIL on the new cases.

- [ ] **Step 3: Implement**

`VerdictBanner.tsx` `slabFor`, before `default`:

```tsx
    case "DnsDown":
      return {
        cls: "linkdown", icon: "down", headline: "Your internet is UP, but name lookups are failing",
        caveat: (verdict.detail ?? "").replace(/^DnsDown:\s*/, "") || "your resolver isn't answering",
        mark: { shape: "bar", hue: "grey", depth: "out" },
      };
```

`TrayStatus.cs`: `"LinkDown" or "DnsDown" => TrayState.Down,`. `Builders.cs`: `verdict is "LinkDown" or "DnsDown" ? "OFF" : "ON"` with the doc comment "ON unless the verdict says the LINK is down or name lookups are failing (spec 2026-10-10 D9)". `README.md`: add a short "Name resolution" paragraph to the section that explains how the verdict is decided (search for "How the verdict is decided"; if that heading does not exist yet, add it under the Privacy section's neighbour "## How it decides"): three sentences — the resolvers your machine uses are asked for a few well-known names; when the anchors answer but every one of those resolvers is down the verdict is "UP, but name lookups are failing" and the banner says whether it is your resolver or every resolver; a resolver that blocks the seeded names (Pi-hole) is shown as filtered, not down.

- [ ] **Step 4: Run everything**

Run: `dotnet test ConnectionWatcher.sln` and `npm --prefix Programs/ConWatch/ClientApp test -- --run`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/ClientApp/src/components/VerdictBanner.tsx Programs/ConWatch/ClientApp/src/components/VerdictBanner.test.ts Libraries/ConInfrastructure/Tray/TrayStatus.cs Libraries/ConNotify/Builders.cs README.md Tests/ConWatch.Tests/TrayStatusTests.cs Tests/ConNotify.Tests
git commit -m "feat(dns): DnsDown reaches the banner, the tray colour and the MQTT internet binary; README explains the rule"
```

---

## Self-review notes

- **Spec coverage (rollout step 2):** §4.2 D11 → Task 1; §4.3 D10 → Task 2; §4.4 verdict, wait, blame, LinkDown suffix, Degraded/Filtered summary → Tasks 3–4; §4.5 five notes → Task 5, two health codes → Task 6 (`dns-over-https` deferred to Plan 3 by ruling, stated in Global Constraints); §5.1 `dns` block, MCP, metrics, report line, history scope → Tasks 6–7; §5.2/§5.4 one-liners → Task 8; the rest of §5.2–5.4 is Plans 3–4.
- **Type consistency:** `DnsResult(At, Success, Outcome, Name, Message)` used by `DnsSummary.IsFiltered`, the notes and the status builder; `DnsWatch(Enabled, System, Upstream)` consumed by the verdict, the model check, `DnsSummary` and the status builder; `VerdictSnapshot.Dns` is `DnsBlame?` with `Kind`, `Message`, `Resolvers`; `StatusSnapshotBuilder.Build` gains a third parameter and every caller passes `DnsLookupNames.Current`.
- **Review Focus:** 1 → Tasks 3 and 5; 2 → Task 4; 3 → Tasks 3 and 4 (`watch: false`); 4 → Task 5; 5 → Task 2.
- **Binding resolution inside Task 3:** `IsFiltered` judges per name (a name is blocked while its latest judged result is `Filtered`); the test text was adjusted accordingly before any run.
