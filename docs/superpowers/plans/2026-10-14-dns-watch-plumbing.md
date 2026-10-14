# DNS Watch — Plan 1: Plumbing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the engine a DNS probe kind, a `Resolver` target scope, a `SystemManaged` target flag, seeded lookup names, and a host service that keeps one Resolver target per OS-configured resolver, so that resolvers are probed like any other target and show in `targets[]`, while the verdict stays exactly as it is today.

**Architecture:** The DNS probe is an `IPingTransceiver` the existing factory returns for `ProbeKind.Dns`, so `PingController`, `TargetMonitor`, thresholds and bursts see DNS queries as ordinary samples. Resolver targets are parentless `TargetScope.Resolver` targets, kept out of the tree, the loss windows and the suspicion sweep like Local ones. A host-side `SystemResolverService` reads the OS resolver list and reconciles it into the stored config through `ConfigService.EditDocumentAsync`, which rebuilds the engine the way any config edit does. Lookup names live in two settings keys and reach the transceiver through a `DnsLookupNames` singleton the host refreshes before every engine start.

**Tech Stack:** .NET 10, EF Core + SQLite (migrations), System.Net.Sockets UDP, xUnit, React + vitest for the one client change.

**Spec:** `docs/superpowers/specs/2026-10-10-dns-watch-design.md` (§4.1–§4.3, §5.5, rollout step 1). This plan is step 1 of 4; the verdict (`DnsDown`), notes, UI and consumers are later plans.

## Global Constraints

- Verdict behaviour is unchanged by this plan. `VerdictCoordinator` is not touched; Resolver targets never reach it as evidence.
- Resolver targets have **no parent** (spec R1). The validator rejects one.
- Resolver samples never reach `LossWindows`, `SuspicionCoordinator` or glitches (spec R2); the pump guard becomes `Scope == Verdict`.
- Probe string for DNS is exactly `dns`; port 53 is implied. Stored as `"dns"`, never null.
- Seeded lookup names and expected addresses (spec R3): `dns.google` → 8.8.8.8, 8.8.4.4; `one.one.one.one` → 1.1.1.1, 1.0.0.1; `dns.quad9.net` → 9.9.9.9, 149.112.112.112; `dns.opendns.com` → 208.67.222.222, 208.67.220.220.
- Settings keys: `Dns.Enabled` ("true"/"false", default "true"), `Dns.LookupNames` (JSON array), `Dns.RetiredResolvers` (newline-separated addresses, per-node, maintained by the service, not editable through the API).
- System resolver target names: `Your resolver {address}`, or `Docker's resolver` for `127.0.0.11`.
- Built-in profile `Resolver`: NormalInterval 30 s, PingTimeout 5 s, BurstInterval 2 s, BurstMaxDuration 60 s, HistorySize 30, MinUsableSample 6, Responding 0.80, Faltering 0.50, Down 0.10, Recovering 0.25.
- DNS burst timeout cap in `WatcherEngine.StartController`: 2000 ms.
- `PingResult.Message` values the DNS transceiver produces (later plans read them): `Answered`, `NxDomain`, `ServFail`, `RcodeRefused`, `Rcode{n}`, `WrongAnswer: {detail}`, `TimedOut`, `Malformed`, `NoLookupNames`, or a `SocketError` name. Never the bare word `Refused`, which `TargetMonitor` counts as a TCP refusal.
- Repo rules: tests wait for conditions, never sleep-then-assert; no personal endpoints in commits; commit after each task; never push.
- Test commands: `dotnet test Tests/<Project> --filter "FullyQualifiedName~<Class>"`; client tests `npm test -- --run` in `Programs/ConWatch/ClientApp`.

## Review Focus

Inputs the spec implies but no task's tests would otherwise exercise. Each line's test is added to the owning task.

1. **A stray UDP reply with another query's ID** (a late answer to the previous probe) must not be taken as this probe's answer. Task 7 tests a wrong-ID reply followed by the right one.
2. **An OS resolver list that throws** (no interfaces, a missing `/etc/resolv.conf` inside a minimal container) must read as "none found", not crash the service or retire everything. Task 8 tests a throwing source; Task 9 tests that an empty list retires rather than deletes.
3. **A duplicate target name** when the OS hands back an address whose generated name already exists (an admin named their own target "Your resolver 10.1.10.1"). Task 9 tests the " (2)" suffix.
4. **An admin who disabled a system resolver** must not have it re-enabled by the next reconcile. Task 9 tests that a disabled-and-present target outside the retired set stays disabled.
5. **A response claiming more answers than it carries** (ANCOUNT past the end of the datagram) must parse as malformed, not throw. Task 6 tests a truncated datagram.

## File Structure

**Create**
- `Core/ConCore/Dns/DnsLookupName.cs` — the lookup-name record, the seeded list, JSON (de)serialization.
- `Core/ConCore/Dns/IDnsLookupNames.cs` — what the transceiver asks: the next name for a resolver.
- `Core/ConEngine/Dns/DnsResultLog.cs` — last 20 DNS results per Resolver target, in memory.
- `Libraries/ConInfrastructure/Dns/DnsLookupNames.cs` — the host-refreshed `IDnsLookupNames` with per-resolver round-robin.
- `Libraries/ConInfrastructure/Dns/DnsMessage.cs` — wire format: build an A query, parse an answer.
- `Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs` — one UDP query per sample, classified into a `PingResult`.
- `Libraries/ConInfrastructure/Dns/SystemResolverSource.cs` — the OS resolver list (Windows NICs, Linux `resolv.conf`).
- `Libraries/ConPersistence/Migrations/<stamp>_AddDnsWatch.cs` (+ Designer, generated) — `Targets.SystemManaged`.
- `Programs/ConWatch/Dns/SystemResolverReconciler.cs` — pure: stored targets + OS list + retired set → new target list.
- `Programs/ConWatch/Dns/SystemResolverService.cs` — hosted: runs the reconciler at start, every 5 min, on network/settings change.
- Tests: `Tests/ConEngine.Tests/ResolverScopeValidationTests.cs`, `ResolverScopeEngineTests.cs`, `DnsLookupNamesTests.cs`, `DnsMessageTests.cs`, `DnsQueryTransceiverTests.cs`, `SystemResolverSourceTests.cs`, `DnsResultLogTests.cs`, `Fakes/FakeDnsServer.cs`; `Tests/ConPersistence.Tests/TargetSystemManagedTests.cs`, `DnsSettingsTests.cs`; `Tests/ConWatch.Tests/FakeSystemResolverSource.cs`, `SystemResolverReconcilerTests.cs`, `SystemResolverServiceTests.cs`, `CloneWithoutSystemManagedTests.cs`.

**Modify**
- `Core/ConCore/ProbeSpec.cs` — `ProbeKind.Dns`, parse/print `dns`.
- `Core/ConCore/Targets/TargetScope.cs` — `Resolver = 2`.
- `Core/ConCore/Targets/Target.cs`, `Core/ConEngine/Configuration/WatcherOptions.cs` (`TargetConfig`), `Libraries/ConPersistence/Config/ConfigDtos.cs` (`TargetDto`), `Libraries/ConPersistence/Entities/ConfigRows.cs` (`TargetRow`), `Libraries/ConPersistence/Config/ConfigMapping.cs`, `Core/ConEngine/Topology/TargetGraph.cs` — `SystemManaged`.
- `Core/ConCore/Targets/PingProfile.cs`, `Libraries/ConPersistence/Config/BuiltInProfiles.cs` — the `Resolver` preset.
- `Core/ConEngine/Configuration/WatcherOptionsValidator.cs` — Resolver rules, SystemManaged rule, probe message.
- `Core/ConEngine/WatcherEngine.cs` — pump guard, `IsSideSubject`, DNS burst cap, `DnsResults`.
- `Core/ConEngine/Topology/ModelCheckCoordinator.cs` — `ProbeText` "dns".
- `Libraries/ConInfrastructure/PingTransceiverFactoryIcmp.cs` — returns the DNS transceiver.
- `Libraries/ConNotify/WebhookNotifier.cs` — filter side subjects, not only Local.
- `Libraries/ConPersistence/Config/ConfigService.cs` — system-managed guards.
- `Libraries/ConPersistence/Settings/SettingsStore.cs` (`SettingKeys`), `SettingsDefaults.cs` — the three keys.
- `Programs/ConWatch/Api/SettingsEndpoints.cs` — validation for the keys.
- `Programs/ConWatch/WatcherHostService.cs` — refresh lookup names before each engine start.
- `Programs/ConWatch/Startup/ConWatchServices.cs` — DI for names, source, service.
- `Programs/ConWatch/StatusSnapshotBuilder.cs` — `SystemManaged` on `StatusTarget`.
- `Programs/ConWatch/Pages/Config/Targets.cshtml`, `Programs/ConWatch/Pages/Index.cshtml.cs`, `Programs/ConWatch/ClientApp/src/lib/lan.ts`, `src/lib/types.ts`, `src/dashboard.tsx` — keep Resolver targets out of the tree and the Targets table.
- `Programs/ConWatch/Clone/CloneService.cs` — always strip system-managed targets from a snapshot.
- `Tests/ConWatch.Tests/ConWatchFactory.cs` — fake resolver source.
- `Tests/ConEngine.Tests/ProbeSpecTests.cs`, `PingProfileTests.cs`; `Tests/ConPersistence.Tests/SettingsDefaultsTests.cs`; `Programs/ConWatch/ClientApp/src/lib/lan.test.ts`.
- `README.md` — privacy list.

---

### Task 1: `ProbeKind.Dns`

**Files:**
- Modify: `Core/ConCore/ProbeSpec.cs`
- Modify: `Core/ConEngine/Configuration/WatcherOptionsValidator.cs:28-30`
- Modify: `Core/ConEngine/Topology/ModelCheckCoordinator.cs:268-273` (`ProbeText`)
- Modify: `Core/ConEngine/WatcherEngine.cs:318-327` (`StartController`)
- Test: `Tests/ConEngine.Tests/ProbeSpecTests.cs`

**Interfaces:**
- Produces: `ProbeKind.Dns`; `ProbeSpec.TryParse("dns")` → `new ProbeSpec(ProbeKind.Dns)`; `ToString()`/`ToConfigString()` → `"dns"`; `ProbePolicy.IsAllowed` true for DNS on any address.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/ConEngine.Tests/ProbeSpecTests.cs`:

```csharp
    [Theory]
    [InlineData("dns")]
    [InlineData("DNS")]
    [InlineData(" dns ")]
    public void Dns_parses_and_prints_as_dns(string text)
    {
        Assert.True(ProbeSpec.TryParse(text, out var spec));
        Assert.Equal(new ProbeSpec(ProbeKind.Dns), spec);
        Assert.Equal("dns", spec.ToString());
        Assert.Equal("dns", spec.ToConfigString());
    }

    [Theory]
    [InlineData("dns:53")]
    [InlineData("dns:")]
    public void Dns_takes_no_port(string text) =>
        Assert.False(ProbeSpec.TryParse(text, out _));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("10.1.10.1")]
    [InlineData("dns.example.net")]
    public void Dns_is_allowed_everywhere(string address) =>
        Assert.True(ProbePolicy.IsAllowed(address, new ProbeSpec(ProbeKind.Dns), out _));
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ProbeSpecTests"`
Expected: FAIL. `ProbeKind.Dns` does not exist (compile error).

- [ ] **Step 3: Implement**

In `Core/ConCore/ProbeSpec.cs`:

```csharp
public enum ProbeKind { Icmp, Tcp, Dns }

/// <summary>
/// How a target is probed: ICMP echo (the default), a TCP connect where
/// any stack response — SYN-ACK or RST — counts as a reply, or a DNS query
/// (UDP 53) where a well-formed answer counts as a reply. Stored in
/// configuration as one optional string ("tcp:80", "dns"); absent means
/// ICMP, so every pre-existing config, snapshot, and seed is valid unchanged.
/// </summary>
public readonly record struct ProbeSpec(ProbeKind Kind, int Port = 0)
{
    public static ProbeSpec Icmp => default;
    public static ProbeSpec Dns => new(ProbeKind.Dns);

    public static bool TryParse(string? text, out ProbeSpec spec)
    {
        spec = Icmp;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        var parts = text.Trim().ToLowerInvariant().Split(':');
        if (parts is ["icmp"])
            return true;
        if (parts is ["dns"])
        {
            spec = Dns;
            return true;
        }
        if (parts is ["tcp", var portText] &&
            int.TryParse(portText, out var port) && port is >= 1 and <= 65535)
        {
            spec = new ProbeSpec(ProbeKind.Tcp, port);
            return true;
        }
        return false;
    }

    public override string ToString() => Kind switch
    {
        ProbeKind.Tcp => $"tcp:{Port}",
        ProbeKind.Dns => "dns",
        _ => "icmp",
    };

    /// <summary>The storage form: null for the ICMP default.</summary>
    public string? ToConfigString() => Kind == ProbeKind.Icmp ? null : ToString();
}
```

`ProbePolicy.IsAllowed` already returns true for any non-TCP kind; leave it, the test pins it.

In `WatcherOptionsValidator.cs` change the probe message:

```csharp
                failures.Add($"Target '{t.Name}' has an invalid Probe '{t.Probe}' (expected 'tcp:<port>' or 'dns').");
```

In `ModelCheckCoordinator.ProbeText`:

```csharp
    /// <summary>"tcp/80" for a TCP probe, "dns" for a DNS query, "ping" for ICMP echo.</summary>
    private static string ProbeText(Target target)
    {
        ProbeSpec.TryParse(target.Probe, out var probe);
        return probe.Kind switch
        {
            ProbeKind.Tcp => $"tcp/{probe.Port}",
            ProbeKind.Dns => "dns",
            _ => "ping",
        };
    }
```

In `WatcherEngine.StartController` replace the burst cap line:

```csharp
        // Bursts shorten the timeout — but a refused TCP connect takes ~2s to
        // surface on Windows (the stack retries after the RST), so TCP probes
        // keep enough budget to classify Refused as a reply during bursts. A
        // recursive DNS answer can take a second or two when nothing is cached.
        var burstCapMs = probe.Kind switch { ProbeKind.Tcp => 2500, ProbeKind.Dns => 2000, _ => 1000 };
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ProbeSpecTests|FullyQualifiedName~WatcherOptionsValidatorTests|FullyQualifiedName~ModelCheckCoordinatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConCore/ProbeSpec.cs Core/ConEngine/Configuration/WatcherOptionsValidator.cs Core/ConEngine/Topology/ModelCheckCoordinator.cs Core/ConEngine/WatcherEngine.cs Tests/ConEngine.Tests/ProbeSpecTests.cs
git commit -m "feat(probe): dns probe kind ('dns', port 53 implied); 2 s burst cap"
```

---

### Task 2: `TargetScope.Resolver`

**Files:**
- Modify: `Core/ConCore/Targets/TargetScope.cs`
- Modify: `Core/ConEngine/Configuration/WatcherOptionsValidator.cs:16,42-50,63-73,78-83`
- Modify: `Core/ConEngine/WatcherEngine.cs:115-122,268-274`
- Modify: `Libraries/ConNotify/WebhookNotifier.cs:35-37`
- Test: `Tests/ConEngine.Tests/ResolverScopeValidationTests.cs`, `Tests/ConEngine.Tests/ResolverScopeEngineTests.cs`

**Interfaces:**
- Produces: `TargetScope.Resolver`; `WatcherEngine.IsSideSubject(Guid)` true for Local and Resolver targets (false for pools, the verdict, unknown ids, before start). `IsLocalSubject` stays.

- [ ] **Step 1: Write the failing validator tests**

Create `Tests/ConEngine.Tests/ResolverScopeValidationTests.cs`:

```csharp
using ConCore.Targets;
using ConEngine.Configuration;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 §4.1, R1: a Resolver target stands alone — no parent,
/// never a pool member or a pool's parent, never anyone's parent.</summary>
public class ResolverScopeValidationTests
{
    private static ValidateOptionsResult Validate(WatcherOptions options) =>
        new WatcherOptionsValidator().Validate(null, options);

    private static TargetConfig Router => new() { Name = "Router", Address = "203.0.113.1", Profile = "Internal" };
    private static TargetConfig Resolver => new()
    {
        Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Public",
        Probe = "dns", Scope = TargetScope.Resolver,
    };

    [Fact]
    public void A_parentless_Resolver_target_is_valid()
    {
        var result = Validate(new WatcherOptions { Targets = [Router, Resolver] });
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void A_Resolver_may_share_the_routers_address_with_a_different_probe()
    {
        // The household resolver usually IS the router; "address + probe" is the uniqueness key.
        var result = Validate(new WatcherOptions { Targets = [Router, Resolver] });
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void A_Resolver_target_cannot_have_a_parent()
    {
        var resolver = Resolver;
        resolver.Parent = "Router";
        var result = Validate(new WatcherOptions { Targets = [Router, resolver] });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("is Resolver and cannot have a parent"));
    }

    [Fact]
    public void A_Resolver_target_cannot_be_a_pool_member()
    {
        var result = Validate(new WatcherOptions
        {
            Targets = [Router, Resolver],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Your resolver 203.0.113.1"] }],
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Resolver targets cannot be pool members"));
    }

    [Fact]
    public void A_Resolver_target_cannot_be_a_parent()
    {
        var child = new TargetConfig { Name = "Child", Address = "203.0.113.2", Parent = "Your resolver 203.0.113.1" };
        var result = Validate(new WatcherOptions { Targets = [Router, Resolver, child] });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Resolver targets cannot have children"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ResolverScopeValidationTests"`
Expected: FAIL. `TargetScope.Resolver` does not exist.

- [ ] **Step 3: Implement the scope and the validator rules**

`Core/ConCore/Targets/TargetScope.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ConCore.Targets;

/// <summary>
/// What a target is for. <see cref="Verdict"/> targets (the default) form the
/// reachability tree and feed the internet verdict. <see cref="Local"/>
/// targets are LAN devices: probed with history, shown in the LAN section,
/// and excluded from the verdict, suspicion, notifications and the ISP report.
/// <see cref="Resolver"/> targets are DNS resolvers (spec 2026-10-10): probed
/// with a DNS query, parentless, shown in the Name resolution section, and
/// excluded from the tree, loss windows, suspicion and notifications; the
/// DNS verdict rule (a later plan) is the only thing that reads them.
/// Serialized as a string everywhere ("Verdict" / "Local" / "Resolver").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetScope>))]
public enum TargetScope
{
    Verdict = 0,
    Local = 1,
    Resolver = 2,
}
```

In `WatcherOptionsValidator.Validate`, replace the `localNames` set with a scope map so the Local rules apply to both side scopes and the messages name the scope. Change line 16:

```csharp
        // Side scopes (Local, Resolver) stand outside the tree: no parent, no
        // children, never in a pool. Name -> scope, for the wording.
        var sideScopes = new Dictionary<string, TargetScope>(StringComparer.OrdinalIgnoreCase);
```

Replace the `if (t.Scope == TargetScope.Local)` block (lines 42–50) with:

```csharp
            if (t.Scope is TargetScope.Local or TargetScope.Resolver)
            {
                if (!string.IsNullOrWhiteSpace(t.Name))
                    sideScopes[t.Name] = t.Scope;
                if (t.Parent is { Length: > 0 })
                    failures.Add($"Target '{t.Name}' is {t.Scope} and cannot have a parent.");
            }
```

Replace the three `localNames` uses in the pool and parent loops:

```csharp
                else if (sideScopes.TryGetValue(m, out var memberScope))
                    failures.Add($"Pool '{p.Name}' member '{m}' is a {memberScope} target; {memberScope} targets cannot be pool members.");
```

```csharp
            else if (p.Parent is { Length: > 0 } && sideScopes.TryGetValue(p.Parent, out var poolParentScope))
                failures.Add($"Pool '{p.Name}' has {poolParentScope} target '{p.Parent}' as parent; {poolParentScope} targets cannot have children.");
```

```csharp
            else if (sideScopes.TryGetValue(t.Parent, out var parentScope))
                failures.Add($"Target '{t.Name}' has {parentScope} target '{t.Parent}' as parent; {parentScope} targets cannot have children.");
```

(With scope `Local` every message reads exactly as before, so `LocalScopeValidationTests` keeps passing.)

- [ ] **Step 4: Run the validator tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ResolverScopeValidationTests|FullyQualifiedName~LocalScopeValidationTests|FullyQualifiedName~WatcherOptionsValidatorTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing engine test**

Create `Tests/ConEngine.Tests/ResolverScopeEngineTests.cs`:

```csharp
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-10 R2: Resolver targets are tracked on the side, like Local ones.</summary>
public class ResolverScopeEngineTests
{
    private static WatcherOptions Options() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "198.51.100.8", Parent = "Router" },
            new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local },
            new TargetConfig
            {
                Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Internal",
                Probe = "dns", Scope = TargetScope.Resolver,
            },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google"] }],
    };

    [Fact]
    public async Task Resolver_samples_stay_out_of_the_loss_windows_and_the_resolver_is_a_side_subject()
    {
        var pings = new FakePingTransceiverFactory();
        var engine = new WatcherEngine(TimeProvider.System, pings,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.False(engine.IsSideSubject(Guid.NewGuid())); // before start: no graph, no throw

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var resolverId = engine.Graph.GetByName("Your resolver 203.0.113.1").Id;
            var routerId = engine.Graph.GetByName("Router").Id;
            Assert.True(engine.IsSideSubject(resolverId));
            Assert.True(engine.IsSideSubject(engine.Graph.GetByName("NAS").Id));
            Assert.False(engine.IsSideSubject(routerId));
            Assert.False(engine.IsSideSubject(engine.Graph.GetPoolByName("Internet").Id));
            Assert.False(engine.IsSideSubject(VerdictCoordinator.SubjectId));
            Assert.False(engine.IsLocalSubject(resolverId)); // Resolver is not Local

            // The resolver's probe was created with the dns spec, and it is being pinged.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (engine.Monitors[resolverId].PingCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(engine.Monitors[resolverId].PingCount > 0);
            Assert.Contains(pings.Created, c => c.Probe == ProbeSpec.Dns);

            // Its samples never land in a loss window; the router's do.
            while (engine.Loss.Open(routerId) is not { Samples: > 0 } && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.True(engine.Loss.Open(routerId) is { Samples: > 0 });
            Assert.Null(engine.Loss.Open(resolverId));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ResolverScopeEngineTests"`
Expected: FAIL. `IsSideSubject` does not exist.

- [ ] **Step 7: Implement the engine side**

In `WatcherEngine.cs`, after `IsLocalSubject`:

```csharp
    /// <summary>
    /// True when <paramref name="subjectId"/> is a target tracked on the side —
    /// Local (a LAN device) or Resolver (a DNS resolver). The notifiers leave
    /// side targets out unless they are critical; the verdict carries what
    /// matters about them. False for pools, the verdict subject, unknown ids
    /// and before the engine starts.
    /// </summary>
    public bool IsSideSubject(Guid subjectId) =>
        _graph is { } graph && graph.Targets.TryGetValue(subjectId, out var target)
        && target.Scope != TargetScope.Verdict;
```

In the result pump replace the guard:

```csharp
                    // Local and Resolver targets are tracked on the side: no
                    // suspicion bursts, corroboration sweeps, glitch episodes
                    // or loss windows (spec 2026-10-02 §3, 2026-10-10 R2).
                    if (monitor.Target.Scope == TargetScope.Verdict)
                    {
                        Suspicion?.OnPingSample(monitor.Target.Id, result);
                        Loss.Record(monitor.Target.Id, result.Timestamp, result.Success);
                    }
```

In `WebhookNotifier.Attach`:

```csharp
                // Side (LAN-device, resolver) targets never notify, unless marked
                // critical (spec §4.7); verdict/pool subjects pass. Criticality is
                // decided now: the engine that knew it may be gone by delivery.
                var critical = engine.IsCriticalSubject(t.TargetId);
                if (critical || !engine.IsSideSubject(t.TargetId))
                    _queue.Writer.TryWrite((t, critical));
```

- [ ] **Step 8: Run the engine and notifier tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~ResolverScopeEngineTests|FullyQualifiedName~LocalScopeEngineTests"` then `dotnet test Tests/ConNotify.Tests`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Core/ConCore/Targets/TargetScope.cs Core/ConEngine/Configuration/WatcherOptionsValidator.cs Core/ConEngine/WatcherEngine.cs Libraries/ConNotify/WebhookNotifier.cs Tests/ConEngine.Tests/ResolverScopeValidationTests.cs Tests/ConEngine.Tests/ResolverScopeEngineTests.cs
git commit -m "feat(scope): Resolver targets — parentless, out of loss windows and suspicion, side subjects for notifiers"
```

---

### Task 3: The `Resolver` ping profile

**Files:**
- Modify: `Core/ConCore/Targets/PingProfile.cs:30-44`
- Modify: `Libraries/ConPersistence/Config/BuiltInProfiles.cs`
- Test: `Tests/ConEngine.Tests/PingProfileTests.cs`, `Tests/ConPersistence.Tests/ConfigStoreTests.cs`

**Interfaces:**
- Produces: `PingProfile.Resolver` (name `"Resolver"`), in `PingProfile.BuiltIns`; a description in `BuiltInProfiles.All`, so `ConfigStore.EnsureBuiltInsAsync` adds it to existing databases on start.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/ConEngine.Tests/PingProfileTests.cs`:

```csharp
    [Fact]
    public void Resolver_preset_queries_every_half_minute_and_bursts_on_a_miss()
    {
        var p = PingProfile.Resolver;
        Assert.Equal("Resolver", p.Name);
        Assert.Equal(TimeSpan.FromSeconds(30), p.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), p.PingTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), p.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), p.BurstMaxDuration);
        Assert.Equal(30, p.HistorySize);
        Assert.Equal(6, p.MinUsableSample);
        Assert.Equal(0.80, p.RespondingThreshold);
        Assert.Equal(0.50, p.FalteringThreshold);
        Assert.Equal(0.10, p.DownThreshold);
        Assert.Equal(0.25, p.RecoveringThreshold);
        Assert.Contains(PingProfile.BuiltIns, b => ReferenceEquals(b, p));
        Assert.True(PingProfile.IsBuiltIn("resolver"));
    }
```

Append to `Tests/ConPersistence.Tests/ConfigStoreTests.cs` (the class has `_factory` and `_store` fields):

```csharp
    [Fact]
    public async Task EnsureBuiltIns_adds_the_Resolver_preset_to_an_older_database()
    {
        await _store.SeedAsync(null);
        await using (var db = _factory.CreateDbContext())
        {
            db.Profiles.Remove(await db.Profiles.SingleAsync(p => p.Name == "Resolver"));
            await db.SaveChangesAsync();
        }

        await _store.EnsureBuiltInsAsync();

        await using var check = _factory.CreateDbContext();
        var row = await check.Profiles.SingleAsync(p => p.Name == "Resolver");
        Assert.Equal(30, row.NormalIntervalSeconds);
        Assert.False(string.IsNullOrEmpty(row.Description));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingProfileTests"` and `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: FAIL. `PingProfile.Resolver` does not exist.

- [ ] **Step 3: Implement**

In `PingProfile.cs`, after the `Local` preset:

```csharp
    /// <summary>Preset for DNS resolvers (spec 2026-10-10 §4.1): one query every
    /// half minute, a 2 s burst for a minute on the first miss, the Local-style
    /// small forgiving window. A slow query is not a Faltering; a dead resolver
    /// is Down about a minute after its first miss.</summary>
    public static PingProfile Resolver { get; } = new()
    {
        Name = "Resolver",
        NormalInterval = TimeSpan.FromSeconds(30),
        PingTimeout = TimeSpan.FromSeconds(5),
        BurstInterval = TimeSpan.FromSeconds(2),
        BurstMaxDuration = TimeSpan.FromSeconds(60),
        HistorySize = 30,
        MinUsableSample = 6,
        RespondingThreshold = 0.80,
        FalteringThreshold = 0.50,
        DownThreshold = 0.10,
        RecoveringThreshold = 0.25,
    };

    /// <summary>The presets every configuration has, in display order.</summary>
    public static IReadOnlyList<PingProfile> BuiltIns { get; } = [Internal, Path, Public, Local, Resolver];
```

In `BuiltInProfiles.All` add:

```csharp
        new(PingProfile.Resolver, "DNS resolvers watched by the name-resolution check: one query " +
                                  "every half minute, a burst on the first miss."),
```

Note: `WatcherEngine.StallAfter` is derived from the longest enabled profile interval, so an install with a Resolver target tolerates 95 s of silence instead of 33 s. That is intended.

- [ ] **Step 4: Run the tests**

Run: the two commands from Step 2, plus `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TargetGraphTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConCore/Targets/PingProfile.cs Libraries/ConPersistence/Config/BuiltInProfiles.cs Tests/ConEngine.Tests/PingProfileTests.cs Tests/ConPersistence.Tests/ConfigStoreTests.cs
git commit -m "feat(profiles): Resolver preset (30 s cadence, 2 s burst, forgiving window)"
```

---

### Task 4: The `SystemManaged` flag

**Files:**
- Modify: `Core/ConCore/Targets/Target.cs`, `Core/ConEngine/Configuration/WatcherOptions.cs` (`TargetConfig`), `Libraries/ConPersistence/Config/ConfigDtos.cs` (`TargetDto`), `Libraries/ConPersistence/Entities/ConfigRows.cs` (`TargetRow`), `Libraries/ConPersistence/Config/ConfigMapping.cs:109-148`, `Core/ConEngine/Topology/TargetGraph.cs:44-58`
- Modify: `Core/ConEngine/Configuration/WatcherOptionsValidator.cs` (one rule)
- Modify: `Libraries/ConPersistence/Config/ConfigService.cs:85-168,194-214`
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs`
- Create: migration `AddDnsWatch` (generated)
- Test: `Tests/ConPersistence.Tests/TargetSystemManagedTests.cs`, `Tests/ConPersistence.Tests/ConfigServiceTests.cs`, `Tests/ConPersistence.Tests/MigrationsTests.cs` (existing, must pass)

**Interfaces:**
- Produces: `bool SystemManaged` (default false) on `Target`, `TargetConfig`, `TargetDto`, `TargetRow`, `StatusTarget` (last positional parameter, serialized `systemManaged`). Validator: `SystemManaged` requires `Scope == Resolver`. `ConfigService.DeleteTargetAsync` refuses a system-managed row with `ConfigErrorKind.Conflict`; `UpsertTargetAsync` on a system-managed row allows only `Name`, `Enabled`, `Operator` and `Critical` to change, and the API path can never set the flag (the reconciler sets it through `EditDocumentAsync`).

- [ ] **Step 1: Write the failing persistence tests**

Create `Tests/ConPersistence.Tests/TargetSystemManagedTests.cs`:

```csharp
using System.Text.Json;
using ConCore.Targets;
using ConEngine.Configuration;
using ConPersistence.Config;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Spec 2026-10-10 §4.1: the SystemManaged flag, default false everywhere.</summary>
public class TargetSystemManagedTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;

    public TargetSystemManagedTests() => _store = new ConfigStore(_factory);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SystemManaged_round_trips_through_the_store_and_defaults_to_false()
    {
        await _store.SeedAsync(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig
                {
                    Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Resolver",
                    Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true,
                },
            ],
        });

        var options = await _store.LoadWatcherOptionsAsync();
        Assert.True(options.Targets.Single(t => t.Name.StartsWith("Your resolver")).SystemManaged);
        Assert.False(options.Targets.Single(t => t.Name == "Router").SystemManaged);

        var exported = await _store.ExportAsync();
        Assert.True(exported.Targets.Single(t => t.Name.StartsWith("Your resolver")).SystemManaged);

        await using var db = _factory.CreateDbContext();
        Assert.True((await db.Targets.SingleAsync(t => t.Name.StartsWith("Your resolver"))).SystemManaged);
    }

    [Fact]
    public void SystemManaged_serializes_as_a_boolean()
    {
        var json = JsonSerializer.Serialize(new TargetDto
        {
            Name = "R", Address = "203.0.113.1", Scope = TargetScope.Resolver, Probe = "dns", SystemManaged = true,
        }, Web);
        Assert.Contains("\"systemManaged\":true", json);
        Assert.Contains("\"scope\":\"Resolver\"", json);
    }

    [Fact]
    public void A_target_with_no_systemManaged_key_is_not_managed()
    {
        var dto = JsonSerializer.Deserialize<TargetDto>("{\"name\":\"T\",\"address\":\"203.0.113.5\"}", Web)!;
        Assert.False(dto.SystemManaged);
    }

    [Fact]
    public void SystemManaged_requires_the_Resolver_scope()
    {
        var result = new WatcherOptionsValidator().Validate(null, new WatcherOptions
        {
            Targets = [new TargetConfig { Name = "T", Address = "203.0.113.5", SystemManaged = true }],
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("system-managed but not a Resolver"));
    }
}
```

Append to `Tests/ConPersistence.Tests/ConfigServiceTests.cs` (the class has `_factory`, `_store` and `_service` fields over a `TestDbFactory`):

```csharp
    private static TargetDto ManagedResolver(Guid? id = null) => new()
    {
        Id = id, Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Resolver",
        Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true,
    };

    private async Task<Guid> SeedManagedResolverAsync()
    {
        var result = await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(ManagedResolver());
            return ConfigResult.Ok();
        });
        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        return (await _store.ExportAsync()).Targets.Single(t => t.SystemManaged).Id!.Value;
    }

    [Fact]
    public async Task A_system_managed_target_cannot_be_deleted()
    {
        await _store.SeedAsync(null);
        var id = await SeedManagedResolverAsync();

        var result = await _service.DeleteTargetAsync(id);

        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Conflict, result.Kind);
        Assert.Single((await _store.ExportAsync()).Targets, t => t.SystemManaged);
    }

    [Fact]
    public async Task A_system_managed_target_can_be_disabled_but_not_re_addressed()
    {
        await _store.SeedAsync(null);
        var id = await SeedManagedResolverAsync();

        var disable = await _service.UpsertTargetAsync(ManagedResolver(id) with { Enabled = false });
        Assert.True(disable.Success, string.Join("; ", disable.Errors ?? []));
        Assert.False((await _store.ExportAsync()).Targets.Single(t => t.Id == id).Enabled);

        var move = await _service.UpsertTargetAsync(ManagedResolver(id) with { Address = "203.0.113.2" }, "correction");
        Assert.False(move.Success);
        Assert.Equal(ConfigErrorKind.Validation, move.Kind);

        var reprobe = await _service.UpsertTargetAsync(ManagedResolver(id) with { Probe = null });
        Assert.False(reprobe.Success);

        var unmanage = await _service.UpsertTargetAsync(ManagedResolver(id) with { SystemManaged = false });
        Assert.False(unmanage.Success);
        Assert.True((await _store.ExportAsync()).Targets.Single(t => t.Id == id).SystemManaged);
    }

    [Fact]
    public async Task The_API_path_cannot_create_a_system_managed_target()
    {
        await _store.SeedAsync(null);

        var result = await _service.UpsertTargetAsync(ManagedResolver());

        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        Assert.False((await _store.ExportAsync()).Targets.Single().SystemManaged);
    }
```

(`ConfigResult.Errors` is the record's error list, `ConfigService.cs:13`.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~TargetSystemManagedTests|FullyQualifiedName~ConfigServiceTests"`
Expected: FAIL. `SystemManaged` does not exist.

- [ ] **Step 3: Add the property everywhere**

`Target.cs` (after `Critical`):

```csharp
    /// <summary>Created and retired by ConnectionWatcher itself from the host's
    /// OS resolver list (spec 2026-10-10 §4.1). The admin may disable it but
    /// not re-address or delete it. Always a Resolver-scope target.</summary>
    public bool SystemManaged { get; init; }
```

`TargetConfig` (WatcherOptions.cs):

```csharp
    /// <summary>Owned by the engine's system-resolver reconcile (spec 2026-10-10); never set by hand.</summary>
    public bool SystemManaged { get; set; }
```

`TargetDto` (ConfigDtos.cs):

```csharp
    /// <summary>Created and retired by ConnectionWatcher from the host's OS resolver
    /// list. Read-only through the targets API: the flag is ignored on create and
    /// update. A missing value (older clients/snapshots) means false.</summary>
    public bool SystemManaged { get; init; }
```

`TargetRow` (ConfigRows.cs):

```csharp
    public bool SystemManaged { get; set; }
```

`ConfigMapping.cs`: add `SystemManaged = t.SystemManaged,` to the three `ToDto`/`ToConfig` initializers (lines 109–129) and `row.SystemManaged = dto.SystemManaged;` in `CopyFrom`.

`TargetGraph.FromOptions`: add `SystemManaged = t.SystemManaged,` after `Critical = t.Critical,`.

`StatusSnapshotBuilder.cs`: extend the record and the builder:

```csharp
/// <param name="SystemManaged">Created from the host's OS resolver list; the admin can only disable it.</param>
public sealed record StatusTarget(Guid Id, string Name, string Address, string? Probe, string State,
    bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator,
    TargetScope Scope, bool Critical, Guid? ExplainedBy, bool SystemManaged);
```

and pass `m.Target.SystemManaged` as the last argument in `Build`.

Validator, inside the targets loop after the scope check:

```csharp
            if (t.SystemManaged && t.Scope != TargetScope.Resolver)
                failures.Add($"Target '{t.Name}' is system-managed but not a Resolver.");
```

- [ ] **Step 4: Generate the migration**

Run from the repo root:

```bash
dotnet ef migrations add AddDnsWatch --project Libraries/ConPersistence
```

Open the generated `Libraries/ConPersistence/Migrations/<stamp>_AddDnsWatch.cs` and confirm `Up` is exactly one `AddColumn<bool>("SystemManaged", "Targets", type: "INTEGER", nullable: false, defaultValue: false)` and `Down` drops it. Then run `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~MigrationsTests"` and expect PASS.

- [ ] **Step 5: Guard the config service**

In `ConfigService.DeleteTargetAsync`, right after the null check on `row`:

```csharp
        if (row.SystemManaged)
            return ConfigResult.Fail(ConfigErrorKind.Conflict,
                $"Target '{row.Name}' is managed by ConnectionWatcher (a system resolver); disable it instead of deleting it.");
```

In `UpsertTargetAsync`, inside the `if (dto.Id is { } id)` branch, after `row = existing;` and before the address-change block:

```csharp
            if (existing.SystemManaged)
            {
                // The host's OS resolver list owns this row: only name, enabled,
                // operator and critical are the admin's to change (spec 2026-10-10 §4.1).
                var frozen = new List<string>();
                if (!string.Equals(existing.Address, ConfigMapping.NormalizeAddress(dto.Address), StringComparison.OrdinalIgnoreCase))
                    frozen.Add("address");
                if (parentId is not null) frozen.Add("parent");
                if (existing.ProfileId != profile.Id) frozen.Add("profile");
                if (!string.Equals(existing.Probe, ConfigMapping.NormalizeProbe(dto.Probe), StringComparison.OrdinalIgnoreCase))
                    frozen.Add("probe");
                if (existing.Scope != dto.Scope) frozen.Add("scope");
                if (!dto.SystemManaged) frozen.Add("systemManaged");
                if (frozen.Count > 0)
                    return ConfigResult.Fail(ConfigErrorKind.Validation,
                        $"Target '{existing.Name}' is managed by ConnectionWatcher (a system resolver); " +
                        $"{string.Join(", ", frozen)} cannot be changed. You can disable or rename it.");
            }
```

After `row.CopyFrom(dto, profile.Id, parentId);` add:

```csharp
        // The flag is the reconciler's (EditDocumentAsync), never the API's: a
        // new row is never managed, an existing row keeps what it had.
        row.SystemManaged = dto.Id is not null && row.SystemManaged;
```

(`row.SystemManaged` was just overwritten by `CopyFrom` from the DTO; for an existing managed row the guard above already required `dto.SystemManaged == true`, so the value is correct; for a new row it becomes false.)

Also in `InsertParentAsync` (line 169 onward), the new target is created from a DTO: after its `CopyFrom`, set `SystemManaged = false` the same way. Read the method and apply the same one-liner after its `CopyFrom`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test Tests/ConPersistence.Tests` and `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TargetGraphTests|FullyQualifiedName~WatcherOptionsValidatorTests"` and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~ConfigApiTests|FullyQualifiedName~CriticalFieldTests|FullyQualifiedName~SnapshotShapeTests"`
Expected: PASS. If `SnapshotShapeTests` or an MCP test pins the exact key set of a status target, add `systemManaged` to its expectation.

- [ ] **Step 7: Commit**

```bash
git add Core Libraries/ConPersistence Programs/ConWatch/StatusSnapshotBuilder.cs Tests/ConPersistence.Tests
git commit -m "feat(config): SystemManaged target flag through model, mappings, database (AddDnsWatch) and status; API can only disable or rename a managed target"
```

---

### Task 5: Lookup names and the DNS settings keys

**Files:**
- Create: `Core/ConCore/Dns/DnsLookupName.cs`, `Core/ConCore/Dns/IDnsLookupNames.cs`
- Create: `Libraries/ConInfrastructure/Dns/DnsLookupNames.cs`
- Modify: `Libraries/ConPersistence/Settings/SettingsStore.cs` (`SettingKeys`), `Libraries/ConPersistence/Settings/SettingsDefaults.cs`
- Modify: `Programs/ConWatch/Api/SettingsEndpoints.cs:70-78`
- Modify: `Programs/ConWatch/WatcherHostService.cs` (constructor, `StartEngineAsync`)
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs` (`AddConWatchEngine`)
- Test: `Tests/ConEngine.Tests/DnsLookupNamesTests.cs`, `Tests/ConPersistence.Tests/DnsSettingsTests.cs`, `Tests/ConPersistence.Tests/SettingsDefaultsTests.cs`, `Tests/ConWatch.Tests/SettingsApiTests.cs` (append; the file exists if there is one named like that, else create `DnsSettingsApiTests.cs` with the shown class)

**Interfaces:**
- Produces: `ConCore.Dns.DnsLookupName(string Name, IReadOnlyList<string> Expected)` with `bool HasExpected`, `static IReadOnlyList<DnsLookupName> Seeded`, `static string ToJson(IEnumerable<DnsLookupName>)`, `static IReadOnlyList<DnsLookupName> FromJson(string? json)` (invalid or empty → empty list), `static bool TryFromJson(string? json, out IReadOnlyList<DnsLookupName> names)`.
- `ConCore.Dns.IDnsLookupNames { DnsLookupName? Next(IPAddress resolver); }`.
- `ConInfrastructure.Dns.DnsLookupNames : IDnsLookupNames` with `void Replace(IReadOnlyList<DnsLookupName> names)`, `IReadOnlyList<DnsLookupName> Current`, `static DnsLookupNames Empty`.
- `SettingKeys.DnsEnabled = "Dns.Enabled"`, `SettingKeys.DnsLookupNames = "Dns.LookupNames"`, `SettingKeys.DnsRetiredResolvers = "Dns.RetiredResolvers"` (in `PerNodeKeys`).

- [ ] **Step 1: Write the failing core tests**

Create `Tests/ConEngine.Tests/DnsLookupNamesTests.cs`:

```csharp
using System.Net;
using ConCore.Dns;
using ConInfrastructure.Dns;

namespace ConEngine.Tests;

public class DnsLookupNamesTests
{
    [Fact]
    public void Seeded_names_are_the_four_anchor_operators_with_their_published_addresses()
    {
        var seeded = DnsLookupName.Seeded;
        Assert.Equal(["dns.google", "one.one.one.one", "dns.quad9.net", "dns.opendns.com"], seeded.Select(n => n.Name));
        Assert.Equal(["8.8.8.8", "8.8.4.4"], seeded[0].Expected);
        Assert.Equal(["1.1.1.1", "1.0.0.1"], seeded[1].Expected);
        Assert.Equal(["9.9.9.9", "149.112.112.112"], seeded[2].Expected);
        Assert.Equal(["208.67.222.222", "208.67.220.220"], seeded[3].Expected);
        Assert.All(seeded, n => Assert.True(n.HasExpected));
    }

    [Fact]
    public void Json_round_trips_and_garbage_reads_as_empty()
    {
        var json = DnsLookupName.ToJson(DnsLookupName.Seeded);
        Assert.Contains("\"name\":\"dns.google\"", json);
        Assert.Equal(DnsLookupName.Seeded, DnsLookupName.FromJson(json));

        Assert.Empty(DnsLookupName.FromJson(null));
        Assert.Empty(DnsLookupName.FromJson(""));
        Assert.Empty(DnsLookupName.FromJson("not json"));
        Assert.False(DnsLookupName.TryFromJson("not json", out _));
        Assert.True(DnsLookupName.TryFromJson("[]", out var none));
        Assert.Empty(none);

        var admin = DnsLookupName.FromJson("[{\"name\":\"example.org\"}]");
        Assert.Single(admin);
        Assert.Equal("example.org", admin[0].Name);
        Assert.False(admin[0].HasExpected);
    }

    [Fact]
    public void Blank_names_and_unparsable_expected_addresses_are_dropped()
    {
        var names = DnsLookupName.FromJson("[{\"name\":\"  \"},{\"name\":\"a.test\",\"expected\":[\"1.2.3.4\",\"nope\"]}]");
        Assert.Single(names);
        Assert.Equal(["1.2.3.4"], names[0].Expected);
    }

    [Fact]
    public void Next_rotates_per_resolver_and_is_null_when_empty()
    {
        var names = new DnsLookupNames();
        var a = IPAddress.Parse("10.0.0.1");
        var b = IPAddress.Parse("10.0.0.2");
        Assert.Null(names.Next(a));

        names.Replace([new DnsLookupName("x.test", []), new DnsLookupName("y.test", [])]);
        Assert.Equal("x.test", names.Next(a)!.Name);
        Assert.Equal("y.test", names.Next(a)!.Name);
        Assert.Equal("x.test", names.Next(a)!.Name);
        Assert.Equal("x.test", names.Next(b)!.Name); // b has its own cursor

        names.Replace([new DnsLookupName("z.test", [])]);
        Assert.Equal("z.test", names.Next(a)!.Name); // a shorter list never indexes past the end
        Assert.Equal("z.test", names.Next(a)!.Name);

        names.Replace([]);
        Assert.Null(names.Next(a));
        Assert.Same(DnsLookupNames.Empty, DnsLookupNames.Empty);
        Assert.Null(DnsLookupNames.Empty.Next(a));
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsLookupNamesTests"`
Expected: FAIL (compile: `ConCore.Dns` does not exist).

- [ ] **Step 3: Implement the core types**

`Core/ConCore/Dns/DnsLookupName.cs`:

```csharp
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConCore.Dns;

/// <summary>
/// A name the resolver watch asks for (spec 2026-10-10 §4.2). A seeded name
/// carries the addresses it is known to resolve to, so a wrong answer is a
/// miss; an admin-added name has none, so any real answer is a hit.
/// </summary>
public sealed record DnsLookupName(string Name, IReadOnlyList<string> Expected)
{
    public bool HasExpected => Expected.Count > 0;

    /// <summary>The four anchor operators' own names and published addresses (spec R3).</summary>
    public static IReadOnlyList<DnsLookupName> Seeded { get; } =
    [
        new("dns.google", ["8.8.8.8", "8.8.4.4"]),
        new("one.one.one.one", ["1.1.1.1", "1.0.0.1"]),
        new("dns.quad9.net", ["9.9.9.9", "149.112.112.112"]),
        new("dns.opendns.com", ["208.67.222.222", "208.67.220.220"]),
    ];

    private sealed record Dto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("expected")] List<string>? Expected);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The settings-row form: a JSON array of {name, expected[]}.</summary>
    public static string ToJson(IEnumerable<DnsLookupName> names) =>
        JsonSerializer.Serialize(names.Select(n => new Dto(n.Name, n.Expected.Count > 0 ? n.Expected.ToList() : null)), Json);

    /// <summary>Parses the settings-row form; null, blank or invalid JSON reads as no names.</summary>
    public static IReadOnlyList<DnsLookupName> FromJson(string? json) =>
        TryFromJson(json, out var names) ? names : [];

    /// <summary>False when the text is not a JSON array of objects. Blank names
    /// and expected entries that are not IP literals are dropped, not errors.</summary>
    public static bool TryFromJson(string? json, out IReadOnlyList<DnsLookupName> names)
    {
        names = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;
        List<Dto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<Dto>>(json, Json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (dtos is null)
            return false;
        names = dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => new DnsLookupName(
                d.Name!.Trim(),
                (d.Expected ?? []).Where(e => IPAddress.TryParse(e, out _)).Select(e => e.Trim()).ToList()))
            .ToList();
        return true;
    }

    public bool Equals(DnsLookupName? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
        Expected.SequenceEqual(other.Expected);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}
```

`Core/ConCore/Dns/IDnsLookupNames.cs`:

```csharp
using System.Net;

namespace ConCore.Dns;

/// <summary>What a DNS probe asks for: the next lookup name for a given resolver.</summary>
public interface IDnsLookupNames
{
    /// <summary>The next name to ask <paramref name="resolver"/>, round-robin per
    /// resolver so one dead zone cannot fail a resolver on its own; null when
    /// there are no names.</summary>
    DnsLookupName? Next(IPAddress resolver);
}
```

`Libraries/ConInfrastructure/Dns/DnsLookupNames.cs`:

```csharp
using System.Net;
using ConCore.Dns;

namespace ConInfrastructure.Dns;

/// <summary>
/// The live lookup-name list the DNS transceivers read. The host replaces it
/// before every engine start (settings + hostname targets); each resolver
/// address keeps its own cursor so two resolvers are not asked the same name
/// at the same moment.
/// </summary>
public sealed class DnsLookupNames : IDnsLookupNames
{
    public static DnsLookupNames Empty { get; } = new();

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, int> _cursor = new();
    private IReadOnlyList<DnsLookupName> _names = [];

    public IReadOnlyList<DnsLookupName> Current
    {
        get { lock (_lock) return _names; }
    }

    public void Replace(IReadOnlyList<DnsLookupName> names)
    {
        lock (_lock)
            _names = names.ToList();
    }

    public DnsLookupName? Next(IPAddress resolver)
    {
        lock (_lock)
        {
            if (_names.Count == 0)
                return null;
            var i = _cursor.GetValueOrDefault(resolver) % _names.Count;
            _cursor[resolver] = i + 1;
            return _names[i];
        }
    }
}
```

- [ ] **Step 4: Run the core tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsLookupNamesTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing settings tests**

Create `Tests/ConPersistence.Tests/DnsSettingsTests.cs`:

```csharp
using ConCore.Dns;
using ConEngine.Configuration;
using ConPersistence.Settings;

namespace ConPersistence.Tests;

public class DnsSettingsTests
{
    [Fact]
    public void Defaults_seed_the_watch_on_with_the_four_names()
    {
        var defaults = SettingsDefaults.For(new VerdictOptions(), new PersistenceOptions());
        Assert.Equal("true", defaults[SettingKeys.DnsEnabled]);
        Assert.Equal(DnsLookupName.Seeded, DnsLookupName.FromJson(defaults[SettingKeys.DnsLookupNames]));
        Assert.False(defaults.ContainsKey(SettingKeys.DnsRetiredResolvers)); // the service writes it, nothing seeds it
    }

    [Fact]
    public void Retired_resolvers_are_per_node() =>
        Assert.Contains(SettingKeys.DnsRetiredResolvers, SettingKeys.PerNodeKeys);
}
```

Update `SettingsDefaultsTests.For_lists_every_seeded_key_from_the_options`: add to the expected dictionary

```csharp
            [SettingKeys.DnsEnabled] = "true",
            [SettingKeys.DnsLookupNames] = DnsLookupName.ToJson(DnsLookupName.Seeded),
```

(and `using ConCore.Dns;`).

Create `Tests/ConWatch.Tests/DnsSettingsApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using ConPersistence.Settings;

namespace ConWatch.Tests;

public class DnsSettingsApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public DnsSettingsApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Dns_keys_are_validated_and_the_retired_list_is_not_editable()
    {
        var ok = await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            [SettingKeys.DnsEnabled] = "false",
            [SettingKeys.DnsLookupNames] = "[{\"name\":\"example.org\"}]",
        });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);

        var badFlag = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [SettingKeys.DnsEnabled] = "maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, badFlag.StatusCode);

        var badJson = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [SettingKeys.DnsLookupNames] = "not json" });
        Assert.Equal(HttpStatusCode.BadRequest, badJson.StatusCode);

        var retired = await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { [SettingKeys.DnsRetiredResolvers] = "10.0.0.1" });
        Assert.Equal(HttpStatusCode.BadRequest, retired.StatusCode);
    }
}
```

(The settings PUT returns 204 No Content for a valid body.)

- [ ] **Step 6: Run them to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~DnsSettingsTests|FullyQualifiedName~SettingsDefaultsTests"` and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsSettingsApiTests"`
Expected: FAIL (`SettingKeys.DnsEnabled` missing; the API returns 400 "Unknown setting").

- [ ] **Step 7: Implement the keys, defaults, endpoint validation and host refresh**

`SettingKeys` (in `SettingsStore.cs`), after the heartbeat keys:

```csharp
    /// <summary>"false" turns the name-resolution watch off (spec 2026-10-10). Default "true".</summary>
    public const string DnsEnabled = "Dns.Enabled";
    /// <summary>JSON array of {name, expected[]}: the names the resolvers are asked for.</summary>
    public const string DnsLookupNames = "Dns.LookupNames";
    /// <summary>Newline-separated resolver addresses the system-resolver service
    /// itself disabled (gone from the OS list, or the watch was off), so it may
    /// re-enable them and nothing else. Maintained by the service, not edited.</summary>
    public const string DnsRetiredResolvers = "Dns.RetiredResolvers";
```

and add `DnsRetiredResolvers` to `PerNodeKeys`.

`SettingsDefaults.For`: add

```csharp
            [SettingKeys.DnsEnabled] = "true",
            [SettingKeys.DnsLookupNames] = DnsLookupName.ToJson(DnsLookupName.Seeded),
```

(`using ConCore.Dns;`; ConPersistence already references ConCore through ConEngine).

`SettingsEndpoints.cs`, in the validation switch: add `case SettingKeys.DnsEnabled:` to the true/false group, and

```csharp
                    case SettingKeys.DnsLookupNames:
                        if (value is not null && !ConCore.Dns.DnsLookupName.TryFromJson(value, out _))
                            errors.Add("Dns.LookupNames must be a JSON array of {\"name\", \"expected\"[]} objects.");
                        break;
                    case SettingKeys.DnsRetiredResolvers:
                        errors.Add("Dns.RetiredResolvers is maintained by ConnectionWatcher, not edited.");
                        break;
```

`ConWatchServices.AddConWatchEngine`: before the factory registration,

```csharp
        services.AddSingleton<ConInfrastructure.Dns.DnsLookupNames>();
        services.AddSingleton<ConCore.Dns.IDnsLookupNames>(sp => sp.GetRequiredService<ConInfrastructure.Dns.DnsLookupNames>());
```

(The factory learns to take it in Task 7.)

`WatcherHostService`: add a constructor parameter `ConInfrastructure.Dns.DnsLookupNames lookupNames` (after `WarmStartSeeder seeder`), and in `StartEngineAsync`, right after `var options = await store.LoadWatcherOptionsAsync();`:

```csharp
        // The DNS probes ask for the configured lookup names plus every hostname
        // target's name (the resolver has to resolve those anyway). Refreshed
        // here, before the engine that will use them exists; empty when the
        // watch is off, so a stray resolver target reports "NoLookupNames".
        var dnsEnabled = await settingsStore.GetAsync(SettingKeys.DnsEnabled) != "false";
        lookupNames.Replace(dnsEnabled
            ? ConCore.Dns.DnsLookupName.FromJson(await settingsStore.GetAsync(SettingKeys.DnsLookupNames))
                .Concat(options.Targets
                    .Where(t => t.Enabled && t.Scope != TargetScope.Resolver && !System.Net.IPAddress.TryParse(t.Address, out _))
                    .Select(t => new ConCore.Dns.DnsLookupName(t.Address, [])))
                .DistinctBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : []);
```

(add `using ConCore.Targets;` to the file's usings.)

- [ ] **Step 8: Run the tests**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~DnsSettingsTests|FullyQualifiedName~SettingsDefaultsTests|FullyQualifiedName~SettingsStoreTests"` and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~DnsSettingsApiTests|FullyQualifiedName~SettingsApiTests|FullyQualifiedName~HostLifecycleTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Core/ConCore/Dns Libraries/ConInfrastructure/Dns/DnsLookupNames.cs Libraries/ConPersistence/Settings Programs/ConWatch/Api/SettingsEndpoints.cs Programs/ConWatch/WatcherHostService.cs Programs/ConWatch/Startup/ConWatchServices.cs Tests/ConEngine.Tests/DnsLookupNamesTests.cs Tests/ConPersistence.Tests/DnsSettingsTests.cs Tests/ConPersistence.Tests/SettingsDefaultsTests.cs Tests/ConWatch.Tests/DnsSettingsApiTests.cs
git commit -m "feat(dns): seeded lookup names (Dns.LookupNames, Dns.Enabled), per-resolver rotation, host refresh before each engine start"
```

---

### Task 6: DNS wire format

**Files:**
- Create: `Libraries/ConInfrastructure/Dns/DnsMessage.cs`
- Create: `Tests/ConEngine.Tests/Fakes/FakeDnsServer.cs` (the response builder half; the UDP server half is Task 7)
- Test: `Tests/ConEngine.Tests/DnsMessageTests.cs`

**Interfaces:**
- Produces: `ConInfrastructure.Dns.DnsRcode { NoError = 0, FormErr = 1, ServFail = 2, NxDomain = 3, NotImp = 4, Refused = 5 }`; `DnsAnswer(ushort Id, DnsRcode Rcode, IReadOnlyList<IPAddress> Addresses)`; `DnsMessage.BuildQuery(ushort id, string name) : byte[]` (one A/IN question, RD set); `DnsMessage.TryParseAnswer(ReadOnlySpan<byte> datagram, out DnsAnswer answer) : bool` (false for anything that is not a well-formed response); `DnsMessage.TryReadQuestionName(ReadOnlySpan<byte> datagram, out string name)` for the fake server.
- `FakeDnsServer.Response(ushort id, string name, DnsRcode rcode, params IPAddress[] addresses) : byte[]` builds a response independently of `DnsMessage` (explicit byte writes), so the parser tests do not test the parser against itself.

- [ ] **Step 1: Write the response builder and the failing tests**

Create `Tests/ConEngine.Tests/Fakes/FakeDnsServer.cs` with the builder half (the server class is added in Task 7):

```csharp
using System.Net;
using System.Text;
using ConInfrastructure.Dns;

namespace ConEngine.Tests.Fakes;

/// <summary>Wire-format helpers written by hand (RFC 1035 §4.1), independent of
/// DnsMessage, so the parser is tested against the standard and not itself.</summary>
public static partial class FakeDnsWire
{
    public static byte[] Response(ushort id, string name, DnsRcode rcode, params IPAddress[] addresses)
    {
        var bytes = new List<byte>();
        void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
        U16(id);
        U16(0x8180 | (int)rcode);         // QR=1, RD=1, RA=1, RCODE
        U16(1);                            // QDCOUNT
        U16(addresses.Length);             // ANCOUNT
        U16(0); U16(0);                    // NSCOUNT, ARCOUNT
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        U16(1); U16(1);                    // QTYPE A, QCLASS IN
        foreach (var address in addresses)
        {
            bytes.Add(0xC0); bytes.Add(12); // NAME: pointer to the question name at offset 12
            U16(1); U16(1);                 // TYPE A, CLASS IN
            bytes.AddRange([0, 0, 0, 60]);  // TTL 60
            U16(4);                         // RDLENGTH
            bytes.AddRange(address.GetAddressBytes());
        }
        return bytes.ToArray();
    }

    /// <summary>A response whose header promises more answers than it carries.</summary>
    public static byte[] Truncated(ushort id, string name) =>
        Response(id, name, DnsRcode.NoError, IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"))[..^10];
}
```

Create `Tests/ConEngine.Tests/DnsMessageTests.cs`:

```csharp
using System.Net;
using ConEngine.Tests.Fakes;
using ConInfrastructure.Dns;

namespace ConEngine.Tests;

public class DnsMessageTests
{
    [Fact]
    public void BuildQuery_writes_one_A_IN_question_with_recursion_desired()
    {
        var q = DnsMessage.BuildQuery(0x1234, "dns.google");

        Assert.Equal([0x12, 0x34], q[..2]);                 // ID
        Assert.Equal([0x01, 0x00], q[2..4]);                // QR=0, RD=1
        Assert.Equal([0, 1, 0, 0, 0, 0, 0, 0], q[4..12]);   // QDCOUNT 1, rest 0
        Assert.Equal([3, (byte)'d', (byte)'n', (byte)'s', 6, (byte)'g', (byte)'o', (byte)'o', (byte)'g', (byte)'l', (byte)'e', 0],
            q[12..24]);
        Assert.Equal([0, 1, 0, 1], q[24..28]);              // QTYPE A, QCLASS IN
        Assert.Equal(28, q.Length);

        Assert.True(DnsMessage.TryReadQuestionName(q, out var name));
        Assert.Equal("dns.google", name);
    }

    [Fact]
    public void A_NOERROR_response_yields_its_A_records_in_order()
    {
        var bytes = FakeDnsWire.Response(7, "dns.google", DnsRcode.NoError,
            IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4"));

        Assert.True(DnsMessage.TryParseAnswer(bytes, out var answer));
        Assert.Equal(7, answer.Id);
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        Assert.Equal([IPAddress.Parse("8.8.8.8"), IPAddress.Parse("8.8.4.4")], answer.Addresses);
    }

    [Theory]
    [InlineData(DnsRcode.NxDomain)]
    [InlineData(DnsRcode.ServFail)]
    [InlineData(DnsRcode.Refused)]
    public void An_error_rcode_parses_with_no_addresses(DnsRcode rcode)
    {
        Assert.True(DnsMessage.TryParseAnswer(FakeDnsWire.Response(9, "x.test", rcode), out var answer));
        Assert.Equal(rcode, answer.Rcode);
        Assert.Empty(answer.Addresses);
    }

    [Fact]
    public void An_empty_NOERROR_answer_parses_with_no_addresses()
    {
        Assert.True(DnsMessage.TryParseAnswer(FakeDnsWire.Response(9, "x.test", DnsRcode.NoError), out var answer));
        Assert.Equal(DnsRcode.NoError, answer.Rcode);
        Assert.Empty(answer.Addresses);
    }

    [Fact]
    public void A_query_is_not_an_answer()
    {
        Assert.False(DnsMessage.TryParseAnswer(DnsMessage.BuildQuery(1, "x.test"), out _)); // QR=0
    }

    [Fact]
    public void Garbage_short_and_truncated_datagrams_are_malformed_not_exceptions()
    {
        Assert.False(DnsMessage.TryParseAnswer([], out _));
        Assert.False(DnsMessage.TryParseAnswer([1, 2, 3], out _));
        Assert.False(DnsMessage.TryParseAnswer(new byte[64], out _)); // QR=0, so not an answer
        Assert.False(DnsMessage.TryParseAnswer(FakeDnsWire.Truncated(5, "dns.google"), out _));
        var bad = FakeDnsWire.Response(5, "dns.google", DnsRcode.NoError, IPAddress.Parse("8.8.8.8"));
        bad[12] = 0xC0; bad[13] = 0xFF; // name pointer past the end
        Assert.False(DnsMessage.TryParseAnswer(bad, out _));
    }

    [Fact]
    public void Non_A_records_are_skipped()
    {
        // A CNAME before the A record: TYPE 5, RDATA an uncompressed name.
        var bytes = FakeDnsWire.Response(3, "www.test", DnsRcode.NoError, IPAddress.Parse("192.0.2.1")).ToList();
        bytes[7] = 2; // ANCOUNT 2
        var cname = new List<byte> { 0xC0, 12, 0, 5, 0, 1, 0, 0, 0, 60, 0, 6, 4, (byte)'h', (byte)'o', (byte)'s', (byte)'t', 0 };
        var questionEnd = 12 + 1 + 3 + 1 + 4 + 1 + 4; // "www" "test" 0 + QTYPE/QCLASS
        bytes.InsertRange(questionEnd, cname);

        Assert.True(DnsMessage.TryParseAnswer(bytes.ToArray(), out var answer));
        Assert.Equal([IPAddress.Parse("192.0.2.1")], answer.Addresses);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsMessageTests"`
Expected: FAIL (compile: `DnsMessage` missing).

- [ ] **Step 3: Implement**

`Libraries/ConInfrastructure/Dns/DnsMessage.cs`:

```csharp
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ConInfrastructure.Dns;

public enum DnsRcode { NoError = 0, FormErr = 1, ServFail = 2, NxDomain = 3, NotImp = 4, Refused = 5 }

/// <summary>What a resolver answered: the id it echoed, the RCODE, and every A record.</summary>
public sealed record DnsAnswer(ushort Id, DnsRcode Rcode, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Just enough of RFC 1035 for a reachability probe: build one A/IN query
/// with recursion desired, and read a response's id, RCODE and A records.
/// Every read is bounds-checked; a malformed datagram parses as false, never
/// as an exception. Name compression pointers are followed only to skip.
/// </summary>
public static class DnsMessage
{
    private const int HeaderLength = 12;
    private const ushort TypeA = 1;
    private const ushort ClassIn = 1;

    public static byte[] BuildQuery(ushort id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var labels = name.Trim().TrimEnd('.').Split('.');
        var bytes = new byte[HeaderLength + labels.Sum(l => 1 + Encoding.ASCII.GetByteCount(l)) + 1 + 4];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);      // QDCOUNT
        var offset = HeaderLength;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63)
                throw new ArgumentException($"Invalid DNS label in '{name}'.", nameof(name));
            bytes[offset++] = (byte)label.Length;
            offset += Encoding.ASCII.GetBytes(label, bytes.AsSpan(offset));
        }
        bytes[offset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 2), ClassIn);
        return bytes;
    }

    /// <summary>The first question's name, for a fake server to echo. False when malformed.</summary>
    public static bool TryReadQuestionName(ReadOnlySpan<byte> datagram, out string name)
    {
        name = "";
        if (datagram.Length < HeaderLength)
            return false;
        var labels = new List<string>();
        var offset = HeaderLength;
        while (true)
        {
            if (offset >= datagram.Length)
                return false;
            int len = datagram[offset++];
            if (len == 0)
                break;
            if ((len & 0xC0) != 0 || offset + len > datagram.Length)
                return false; // compression in a question we built ourselves: not expected
            labels.Add(Encoding.ASCII.GetString(datagram.Slice(offset, len)));
            offset += len;
        }
        name = string.Join('.', labels);
        return true;
    }

    public static bool TryParseAnswer(ReadOnlySpan<byte> datagram, out DnsAnswer answer)
    {
        answer = null!;
        if (datagram.Length < HeaderLength)
            return false;
        var id = BinaryPrimitives.ReadUInt16BigEndian(datagram);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        if ((flags & 0x8000) == 0)
            return false; // QR=0: a query, not a response
        var rcode = (DnsRcode)(flags & 0x000F);
        int qdCount = BinaryPrimitives.ReadUInt16BigEndian(datagram[4..]);
        int anCount = BinaryPrimitives.ReadUInt16BigEndian(datagram[6..]);

        var offset = HeaderLength;
        for (var i = 0; i < qdCount; i++)
        {
            if (!SkipName(datagram, ref offset) || offset + 4 > datagram.Length)
                return false;
            offset += 4; // QTYPE, QCLASS
        }

        var addresses = new List<IPAddress>();
        for (var i = 0; i < anCount; i++)
        {
            if (!SkipName(datagram, ref offset) || offset + 10 > datagram.Length)
                return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(datagram[offset..]);
            var @class = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 2)..]);
            int rdLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[(offset + 8)..]);
            offset += 10;
            if (offset + rdLength > datagram.Length)
                return false;
            if (type == TypeA && @class == ClassIn && rdLength == 4)
                addresses.Add(new IPAddress(datagram.Slice(offset, 4)));
            offset += rdLength;
        }

        answer = new DnsAnswer(id, rcode, addresses);
        return true;
    }

    /// <summary>Advances past a name (labels, or a compression pointer, or both). False on overrun.</summary>
    private static bool SkipName(ReadOnlySpan<byte> datagram, ref int offset)
    {
        while (true)
        {
            if (offset >= datagram.Length)
                return false;
            int len = datagram[offset];
            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 2 > datagram.Length)
                    return false;
                var target = ((len & 0x3F) << 8) | datagram[offset + 1];
                if (target >= datagram.Length)
                    return false; // a pointer past the end
                offset += 2;
                return true;
            }
            offset += 1;
            if (len == 0)
                return true;
            if ((len & 0xC0) != 0 || offset + len > datagram.Length)
                return false;
            offset += len;
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsMessageTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConInfrastructure/Dns/DnsMessage.cs Tests/ConEngine.Tests/Fakes/FakeDnsServer.cs Tests/ConEngine.Tests/DnsMessageTests.cs
git commit -m "feat(dns): minimal RFC 1035 query builder and bounds-checked answer parser"
```

---

### Task 7: The DNS transceiver

**Files:**
- Create: `Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs`
- Modify: `Libraries/ConInfrastructure/PingTransceiverFactoryIcmp.cs`
- Modify: `Tests/ConEngine.Tests/Fakes/FakeDnsServer.cs` (add the UDP server)
- Test: `Tests/ConEngine.Tests/DnsQueryTransceiverTests.cs`

**Interfaces:**
- Produces: `DnsQueryTransceiver(IPAddress ipAddress, IDnsLookupNames names, CancellationToken cancellationToken, int port = 53) : IPingTransceiver`; `internal static PingResult Classify(IPAddress resolver, DnsLookupName lookup, DnsAnswer answer, long elapsedMs)`.
- `PingTransceiverFactoryIcmp(IDnsLookupNames? lookupNames = null)` returns `DnsQueryTransceiver` for `ProbeKind.Dns`.
- `FakeDnsServer : IDisposable` with `int Port`, `Func<ushort, string, byte[]?> Reply` (null = stay silent), `int Received`.

- [ ] **Step 1: Add the fake server and write the failing tests**

Append to `Tests/ConEngine.Tests/Fakes/FakeDnsServer.cs`:

```csharp
/// <summary>A loopback UDP "resolver" whose reply is scripted per query.</summary>
public sealed class FakeDnsServer : IDisposable
{
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private int _received;

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public int Received => Volatile.Read(ref _received);

    /// <summary>(query id, question name) → the datagram(s) to send back; null sends nothing.</summary>
    public Func<ushort, string, IEnumerable<byte[]>?> Reply { get; set; } = (id, name) =>
        [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("8.8.8.8"))];

    public FakeDnsServer() => _ = Task.Run(ServeAsync);

    private async Task ServeAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var query = await _udp.ReceiveAsync(_cts.Token);
                Interlocked.Increment(ref _received);
                var id = (ushort)((query.Buffer[0] << 8) | query.Buffer[1]);
                DnsMessage.TryReadQuestionName(query.Buffer, out var name);
                var replies = Reply(id, name);
                if (replies is null)
                    continue;
                foreach (var reply in replies)
                    await _udp.SendAsync(reply, query.RemoteEndPoint, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        _cts.Dispose();
    }
}
```

(add `using System.Net.Sockets;` and `using ConCore.Dns;` at the top of the file.)

Create `Tests/ConEngine.Tests/DnsQueryTransceiverTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Dns;
using ConEngine.Tests.Fakes;
using ConInfrastructure;
using ConInfrastructure.Dns;

namespace ConEngine.Tests;

public class DnsQueryTransceiverTests
{
    private static readonly DnsLookupName Google = new("dns.google", ["8.8.8.8", "8.8.4.4"]);
    private static readonly DnsLookupName Admin = new("example.org", []);

    private static DnsLookupNames Names(params DnsLookupName[] names)
    {
        var list = new DnsLookupNames();
        list.Replace(names);
        return list;
    }

    private static DnsQueryTransceiver Probe(FakeDnsServer server, DnsLookupNames names) =>
        new(IPAddress.Loopback, names, CancellationToken.None, server.Port);

    [Fact]
    public async Task An_expected_address_is_Answered()
    {
        using var server = new FakeDnsServer();
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal("Answered", result.Message);
        Assert.Equal(1, server.Received);
    }

    [Fact]
    public async Task A_wrong_address_for_a_seeded_name_is_a_miss()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("10.0.0.1"))];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal("WrongAnswer: 10.0.0.1", result.Message);
    }

    [Fact]
    public async Task NXDOMAIN_is_a_miss_for_a_seeded_name_but_a_hit_for_an_admin_name()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, DnsRcode.NxDomain)];

        using var seeded = Probe(server, Names(Google));
        var miss = await seeded.PingAsync(2000);
        Assert.False(miss.Success);
        Assert.Equal("WrongAnswer: NXDOMAIN", miss.Message);

        using var admin = Probe(server, Names(Admin));
        var hit = await admin.PingAsync(2000);
        Assert.True(hit.Success);
        Assert.Equal("NxDomain", hit.Message);
    }

    [Theory]
    [InlineData(DnsRcode.ServFail, "ServFail")]
    [InlineData(DnsRcode.Refused, "RcodeRefused")]
    [InlineData(DnsRcode.NotImp, "Rcode4")]
    public async Task Error_rcodes_are_misses_named_without_the_bare_word_Refused(DnsRcode rcode, string message)
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) => [FakeDnsWire.Response(id, name, rcode)];
        using var probe = Probe(server, Names(Admin));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
        Assert.NotEqual("Refused", result.Message); // TargetMonitor counts that word as a TCP refusal
    }

    [Fact]
    public async Task Silence_is_TimedOut()
    {
        using var server = new FakeDnsServer();
        server.Reply = (_, _) => null;
        using var probe = Probe(server, Names(Admin));
        var result = await probe.PingAsync(300);
        Assert.False(result.Success);
        Assert.Equal("TimedOut", result.Message);
    }

    [Fact]
    public async Task Garbage_is_Malformed()
    {
        using var server = new FakeDnsServer();
        server.Reply = (_, _) => [new byte[] { 1, 2, 3 }];
        using var probe = Probe(server, Names(Admin));
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal("Malformed", result.Message);
    }

    [Fact]
    public async Task A_stray_reply_with_another_id_is_ignored_and_the_right_one_is_taken()
    {
        using var server = new FakeDnsServer();
        server.Reply = (id, name) =>
        [
            FakeDnsWire.Response((ushort)(id ^ 0x5555), name, DnsRcode.ServFail),
            FakeDnsWire.Response(id, name, DnsRcode.NoError, IPAddress.Parse("8.8.4.4")),
        ];
        using var probe = Probe(server, Names(Google));
        var result = await probe.PingAsync(2000);
        Assert.True(result.Success);
        Assert.Equal("Answered", result.Message);
    }

    [Fact]
    public async Task No_lookup_names_is_a_miss_that_sends_nothing()
    {
        using var server = new FakeDnsServer();
        using var probe = Probe(server, Names());
        var result = await probe.PingAsync(2000);
        Assert.False(result.Success);
        Assert.Equal("NoLookupNames", result.Message);
        Assert.Equal(0, server.Received);
    }

    [Fact]
    public async Task Names_rotate_across_probes()
    {
        using var server = new FakeDnsServer();
        var asked = new List<string>();
        server.Reply = (id, name) => { lock (asked) asked.Add(name); return [FakeDnsWire.Response(id, name, DnsRcode.NoError)]; };
        using var probe = Probe(server, Names(Admin, new DnsLookupName("second.test", [])));
        await probe.PingAsync(2000);
        await probe.PingAsync(2000);
        await probe.PingAsync(2000);
        Assert.Equal(["example.org", "second.test", "example.org"], asked);
    }

    [Fact]
    public void The_factory_returns_the_dns_transceiver_for_the_dns_probe()
    {
        var factory = new PingTransceiverFactoryIcmp(DnsLookupNames.Empty);
        using var dns = factory.Create(IPAddress.Loopback, ProbeSpec.Dns, CancellationToken.None);
        Assert.IsType<DnsQueryTransceiver>(dns);
        using var icmp = factory.Create(IPAddress.Loopback, ProbeSpec.Icmp, CancellationToken.None);
        Assert.IsNotType<DnsQueryTransceiver>(icmp);
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsQueryTransceiverTests"`
Expected: FAIL (compile: `DnsQueryTransceiver` missing).

- [ ] **Step 3: Implement**

`Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ConCore;
using ConCore.Dns;

namespace ConInfrastructure.Dns;

/// <summary>
/// DNS probe (spec 2026-10-10 §4.2): one UDP query per sample for the next
/// lookup name, a well-formed answer is the reply. A seeded name must come
/// back with one of its expected addresses — a lying resolver (captive portal,
/// interception) is a miss, not a hit. The timeout races the receive because
/// a dead resolver simply never answers. Messages never use the bare word
/// "Refused", which TargetMonitor counts as a TCP refusal.
/// </summary>
public sealed class DnsQueryTransceiver(IPAddress ipAddress, IDnsLookupNames names,
    CancellationToken cancellationToken, int port = 53) : IPingTransceiver
{
    public IPAddress IpAddress => ipAddress;

    public async Task<PingResult> PingAsync(int timeoutMs)
    {
        var lookup = names.Next(ipAddress);
        if (lookup is null)
            return new PingResult(ipAddress, false, 0, "NoLookupNames");

        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = DnsMessage.BuildQuery(id, lookup.Name);
        var stopwatch = Stopwatch.StartNew();
        using var udp = new UdpClient(ipAddress.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            udp.Connect(new IPEndPoint(ipAddress, port));
            await udp.SendAsync(query, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (!DnsMessage.TryParseAnswer(received.Buffer, out var answer))
                    return new PingResult(ipAddress, false, 0, "Malformed");
                if (answer.Id != id)
                    continue; // a late answer to an earlier query: not ours
                return Classify(ipAddress, lookup, answer, stopwatch.ElapsedMilliseconds);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PingResult(ipAddress, false, 0, "TimedOut"); // our own timeout token
        }
        catch (SocketException ex)
        {
            // ICMP port-unreachable surfaces here (ConnectionReset): the host is up, nothing listens on 53.
            return new PingResult(ipAddress, false, 0, ex.SocketErrorCode.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PingResult(ipAddress, false, 0, ex.Message);
        }
    }

    internal static PingResult Classify(IPAddress resolver, DnsLookupName lookup, DnsAnswer answer, long elapsedMs)
    {
        if (lookup.HasExpected)
        {
            if (answer.Rcode == DnsRcode.NoError && answer.Addresses.Any(a => lookup.Expected.Contains(a.ToString())))
                return new PingResult(resolver, true, elapsedMs, "Answered");
            var detail = answer.Rcode switch
            {
                DnsRcode.NoError when answer.Addresses.Count > 0 => string.Join(", ", answer.Addresses),
                DnsRcode.NoError => "no address",
                DnsRcode.NxDomain => "NXDOMAIN",
                DnsRcode.ServFail => "SERVFAIL",
                DnsRcode.Refused => "REFUSED",
                var other => $"RCODE {(int)other}",
            };
            return answer.Rcode is DnsRcode.NoError or DnsRcode.NxDomain
                ? new PingResult(resolver, false, 0, $"WrongAnswer: {detail}")
                : Miss(answer.Rcode);
        }
        return answer.Rcode switch
        {
            DnsRcode.NoError => new PingResult(resolver, true, elapsedMs, "Answered"),
            DnsRcode.NxDomain => new PingResult(resolver, true, elapsedMs, "NxDomain"),
            var rcode => Miss(rcode),
        };

        PingResult Miss(DnsRcode rcode) => new(resolver, false, 0, rcode switch
        {
            DnsRcode.ServFail => "ServFail",
            DnsRcode.Refused => "RcodeRefused",
            var other => $"Rcode{(int)other}",
        });
    }

    public void Dispose() { }
}
```

`PingTransceiverFactoryIcmp.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Dns;
using ConInfrastructure.Dns;

namespace ConInfrastructure;

/// <summary>The production transceivers: ICMP echo, TCP connect, or a DNS query
/// that asks the host-maintained lookup names (none given: every DNS probe
/// reports "NoLookupNames").</summary>
public class PingTransceiverFactoryIcmp(IDnsLookupNames? lookupNames = null) : IPingTransceiverFactory
{
    private readonly IDnsLookupNames _lookupNames = lookupNames ?? DnsLookupNames.Empty;

    public IPingTransceiver Create(IPAddress ipAddress, ProbeSpec probe, CancellationToken cancellationToken) =>
        probe.Kind switch
        {
            ProbeKind.Tcp => new TcpConnectTransceiver(ipAddress, probe.Port, cancellationToken),
            ProbeKind.Dns => new DnsQueryTransceiver(ipAddress, _lookupNames, cancellationToken),
            _ => new PingTransceiverIcmp(ipAddress, cancellationToken),
        };
}
```

Every existing `new PingTransceiverFactoryIcmp()` keeps compiling (optional parameter). DI resolves `IDnsLookupNames` from Task 5's registration.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsQueryTransceiverTests|FullyQualifiedName~TcpConnectTransceiverTests|FullyQualifiedName~PingTransceiverIcmpTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs Libraries/ConInfrastructure/PingTransceiverFactoryIcmp.cs Tests/ConEngine.Tests/Fakes/FakeDnsServer.cs Tests/ConEngine.Tests/DnsQueryTransceiverTests.cs
git commit -m "feat(dns): UDP/53 query transceiver — expected answers for seeded names, stray ids ignored, no bare 'Refused'"
```

---

### Task 8: The OS resolver list

**Files:**
- Create: `Libraries/ConInfrastructure/Dns/SystemResolverSource.cs`
- Test: `Tests/ConEngine.Tests/SystemResolverSourceTests.cs`

**Interfaces:**
- Produces: `SystemResolver(IPAddress Address, bool IsDockerEmbedded)`; `ISystemResolverSource { IReadOnlyList<SystemResolver> Read(); }`; `SystemResolverSource(Func<IEnumerable<IPAddress>> raw)` with a parameterless production constructor; `internal static IEnumerable<IPAddress> ParseResolvConf(IEnumerable<string> lines)`. `Read()` never throws: a failing source reads as an empty list.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/SystemResolverSourceTests.cs`:

```csharp
using System.Net;
using ConInfrastructure.Dns;

namespace ConEngine.Tests;

public class SystemResolverSourceTests
{
    [Fact]
    public void ParseResolvConf_reads_nameserver_lines_in_order_and_ignores_the_rest()
    {
        var lines = new[]
        {
            "# Generated by NetworkManager",
            "search home.arpa",
            "nameserver 10.1.10.1",
            "nameserver   10.1.10.2   # secondary",
            "options ndots:1",
            "nameserver fe80::1%eth0",
            "nameserver not-an-address",
            "nameserver 127.0.0.11",
        };
        Assert.Equal(
            [IPAddress.Parse("10.1.10.1"), IPAddress.Parse("10.1.10.2"), IPAddress.Parse("fe80::1"), IPAddress.Parse("127.0.0.11")],
            SystemResolverSource.ParseResolvConf(lines));
    }

    [Fact]
    public void Read_dedupes_puts_IPv4_first_and_flags_dockers_resolver()
    {
        var source = new SystemResolverSource(() =>
        [
            IPAddress.Parse("fe80::1"), IPAddress.Parse("127.0.0.11"), IPAddress.Parse("10.1.10.1"), IPAddress.Parse("10.1.10.1"),
        ]);
        var found = source.Read();
        Assert.Equal(
            [IPAddress.Parse("127.0.0.11"), IPAddress.Parse("10.1.10.1"), IPAddress.Parse("fe80::1")],
            found.Select(f => f.Address));
        Assert.True(found[0].IsDockerEmbedded);
        Assert.False(found[1].IsDockerEmbedded);
    }

    [Fact]
    public void A_throwing_source_reads_as_nothing_found()
    {
        var source = new SystemResolverSource(() => throw new IOException("no resolv.conf"));
        Assert.Empty(source.Read());
    }

    [Fact]
    public void The_production_source_does_not_throw_on_this_machine() =>
        _ = new SystemResolverSource().Read();
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~SystemResolverSourceTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`Libraries/ConInfrastructure/Dns/SystemResolverSource.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConInfrastructure.Dns;

/// <summary>One resolver the host is configured to use; Docker's embedded forwarder is flagged for naming.</summary>
public sealed record SystemResolver(IPAddress Address, bool IsDockerEmbedded);

public interface ISystemResolverSource
{
    /// <summary>The resolvers this host resolves through, in the OS's order, IPv4 first. Never throws.</summary>
    IReadOnlyList<SystemResolver> Read();
}

/// <summary>
/// The way this host resolves (spec 2026-10-10 §4.3, D1): Windows' DNS
/// addresses on the interfaces that own a default route, or Linux's
/// resolv.conf nameservers. Inside a Docker bridge network that is 127.0.0.11,
/// Docker's forwarder, which forwards to the host's resolvers — still "the way
/// this host resolves".
/// </summary>
public sealed class SystemResolverSource(Func<IEnumerable<IPAddress>> raw) : ISystemResolverSource
{
    private static readonly IPAddress DockerEmbedded = IPAddress.Parse("127.0.0.11");

    public SystemResolverSource() : this(OperatingSystem.IsWindows() ? WindowsResolvers : LinuxResolvers)
    {
    }

    public IReadOnlyList<SystemResolver> Read()
    {
        IEnumerable<IPAddress> addresses;
        try
        {
            addresses = raw().ToList();
        }
        catch (Exception)
        {
            return []; // no interfaces, no resolv.conf: nothing found, not a crash
        }
        return addresses
            .Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1) // stable: keeps OS order within a family
            .Select(a => new SystemResolver(a, a.Equals(DockerEmbedded)))
            .ToList();
    }

    internal static IEnumerable<IPAddress> WindowsResolvers()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var properties = nic.GetIPProperties();
            if (!properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                continue; // only interfaces that carry a default route resolve for the host
            foreach (var address in properties.DnsAddresses)
                yield return address;
        }
    }

    internal static IEnumerable<IPAddress> LinuxResolvers() =>
        ParseResolvConf(File.ReadLines("/etc/resolv.conf"));

    /// <summary>"nameserver X" lines, in order; comments, options and unparsable entries are skipped;
    /// an IPv6 zone suffix ("%eth0") is dropped.</summary>
    internal static IEnumerable<IPAddress> ParseResolvConf(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Split('#', 2)[0].Split(';', 2)[0].Trim();
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("nameserver", StringComparison.OrdinalIgnoreCase))
                continue;
            var text = parts[1].Split('%', 2)[0];
            if (IPAddress.TryParse(text, out var address))
                yield return address;
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~SystemResolverSourceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConInfrastructure/Dns/SystemResolverSource.cs Tests/ConEngine.Tests/SystemResolverSourceTests.cs
git commit -m "feat(dns): read the host's OS resolver list (Windows NICs, Linux resolv.conf), Docker flag"
```

---

### Task 9: The reconciler (pure)

**Files:**
- Create: `Programs/ConWatch/Dns/SystemResolverReconciler.cs`
- Test: `Tests/ConWatch.Tests/SystemResolverReconcilerTests.cs`

**Interfaces:**
- Produces: `SystemResolverReconciler.Reconcile(IReadOnlyList<TargetDto> targets, IReadOnlyList<SystemResolver> found, bool watchActive, IReadOnlySet<string> retired) : Plan` where `Plan(IReadOnlyList<TargetDto> Targets, IReadOnlySet<string> Retired, bool Changed, IReadOnlyList<string> Log)`; `const string ProfileName = "Resolver"`; `static string NameFor(SystemResolver)`.

Rules (spec §4.3, Task 10 stores `Retired` in `Dns.RetiredResolvers`):
- A found address with no managed target: create one, enabled, when the watch is active (nothing is created while the watch is off).
- A managed target that is enabled but gone from the list, or the watch is off: disable it and remember its address as *retired*.
- A managed target that is disabled, present, watch active, and in the retired set: re-enable it and forget it. Disabled and not in the set: the admin disabled it; leave it.
- Names: `Docker's resolver` or `Your resolver {address}`; a collision gets ` (2)`, ` (3)`, …

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConWatch.Tests/SystemResolverReconcilerTests.cs`:

```csharp
using System.Net;
using ConCore.Targets;
using ConInfrastructure.Dns;
using ConPersistence.Config;
using ConWatch.Dns;

namespace ConWatch.Tests;

public class SystemResolverReconcilerTests
{
    private static SystemResolver Found(string address, bool docker = false) => new(IPAddress.Parse(address), docker);
    private static readonly IReadOnlySet<string> NoneRetired = new HashSet<string>();

    private static TargetDto Managed(string address, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(), Name = $"Your resolver {address}", Address = address, Profile = "Resolver",
        Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true, Enabled = enabled,
    };

    private static readonly TargetDto Router = new() { Id = Guid.NewGuid(), Name = "Router", Address = "10.1.10.1", Profile = "Internal" };

    [Fact]
    public void A_found_address_becomes_an_enabled_managed_Resolver_target()
    {
        var plan = SystemResolverReconciler.Reconcile([Router], [Found("10.1.10.1")], watchActive: true, NoneRetired);

        Assert.True(plan.Changed);
        var created = Assert.Single(plan.Targets, t => t.SystemManaged);
        Assert.Equal("Your resolver 10.1.10.1", created.Name);
        Assert.Equal("10.1.10.1", created.Address);
        Assert.Equal("Resolver", created.Profile);
        Assert.Equal("dns", created.Probe);
        Assert.Equal(TargetScope.Resolver, created.Scope);
        Assert.Null(created.Parent);
        Assert.True(created.Enabled);
        Assert.False(created.Critical);
        Assert.Contains(plan.Targets, t => t.Name == "Router"); // untouched
    }

    [Fact]
    public void Dockers_resolver_is_named_as_such_and_a_name_collision_gets_a_suffix()
    {
        var taken = new TargetDto { Id = Guid.NewGuid(), Name = "Docker's resolver", Address = "203.0.113.5", Profile = "Public" };
        var plan = SystemResolverReconciler.Reconcile([taken], [Found("127.0.0.11", docker: true)], true, NoneRetired);
        Assert.Equal("Docker's resolver (2)", Assert.Single(plan.Targets, t => t.SystemManaged).Name);
    }

    [Fact]
    public void Nothing_is_created_while_the_watch_is_off()
    {
        var plan = SystemResolverReconciler.Reconcile([Router], [Found("10.1.10.1")], watchActive: false, NoneRetired);
        Assert.False(plan.Changed);
        Assert.DoesNotContain(plan.Targets, t => t.SystemManaged);
    }

    [Fact]
    public void A_vanished_address_is_disabled_and_remembered_never_deleted()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([Router, managed], [], true, NoneRetired);

        Assert.True(plan.Changed);
        var kept = Assert.Single(plan.Targets, t => t.SystemManaged);
        Assert.Equal(managed.Id, kept.Id);
        Assert.False(kept.Enabled);
        Assert.Contains("10.1.10.1", plan.Retired);
    }

    [Fact]
    public void A_returning_address_is_re_enabled_only_when_the_service_retired_it()
    {
        var retiredByUs = Managed("10.1.10.1", enabled: false);
        var byAdmin = Managed("10.1.10.2", enabled: false);
        var plan = SystemResolverReconciler.Reconcile([retiredByUs, byAdmin],
            [Found("10.1.10.1"), Found("10.1.10.2")], true, new HashSet<string> { "10.1.10.1" });

        Assert.True(plan.Changed);
        Assert.True(plan.Targets.Single(t => t.Id == retiredByUs.Id).Enabled);
        Assert.False(plan.Targets.Single(t => t.Id == byAdmin.Id).Enabled);
        Assert.DoesNotContain("10.1.10.1", plan.Retired);
    }

    [Fact]
    public void Turning_the_watch_off_retires_every_managed_target_and_on_restores_them()
    {
        var a = Managed("10.1.10.1");
        var b = Managed("10.1.10.2");
        var off = SystemResolverReconciler.Reconcile([a, b], [Found("10.1.10.1"), Found("10.1.10.2")], false, NoneRetired);
        Assert.All(off.Targets.Where(t => t.SystemManaged), t => Assert.False(t.Enabled));
        Assert.Equal(new HashSet<string> { "10.1.10.1", "10.1.10.2" }, off.Retired);

        var on = SystemResolverReconciler.Reconcile(off.Targets, [Found("10.1.10.1"), Found("10.1.10.2")], true, off.Retired);
        Assert.All(on.Targets.Where(t => t.SystemManaged), t => Assert.True(t.Enabled));
        Assert.Empty(on.Retired);
    }

    [Fact]
    public void A_steady_state_is_not_a_change()
    {
        var managed = Managed("10.1.10.1");
        var plan = SystemResolverReconciler.Reconcile([Router, managed], [Found("10.1.10.1")], true, NoneRetired);
        Assert.False(plan.Changed);
        Assert.Same(managed, plan.Targets.Single(t => t.SystemManaged));
    }

    [Fact]
    public void Addresses_match_case_insensitively_and_retired_entries_for_absent_addresses_are_kept()
    {
        var managed = Managed("fe80::1", enabled: false);
        var plan = SystemResolverReconciler.Reconcile([managed], [Found("FE80::1")], true, new HashSet<string> { "fe80::1", "10.9.9.9" });
        Assert.True(plan.Targets.Single().Enabled);
        Assert.Equal(new HashSet<string> { "10.9.9.9" }, plan.Retired); // still gone: still remembered
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~SystemResolverReconcilerTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`Programs/ConWatch/Dns/SystemResolverReconciler.cs`:

```csharp
using ConCore.Targets;
using ConInfrastructure.Dns;
using ConPersistence.Config;

namespace ConWatch.Dns;

/// <summary>
/// Decides what the stored targets should look like given the host's OS
/// resolver list (spec 2026-10-10 §4.3, D7). Pure: the service applies the
/// result. A system-managed target is created when its address appears,
/// disabled (never deleted — history stays) when it disappears or the watch
/// is off, and re-enabled when it returns only if this reconciler disabled it;
/// a target the admin disabled stays disabled.
/// </summary>
public static class SystemResolverReconciler
{
    public const string ProfileName = "Resolver";

    public sealed record Plan(IReadOnlyList<TargetDto> Targets, IReadOnlySet<string> Retired, bool Changed, IReadOnlyList<string> Log);

    public static string NameFor(SystemResolver resolver) =>
        resolver.IsDockerEmbedded ? "Docker's resolver" : $"Your resolver {resolver.Address}";

    public static Plan Reconcile(IReadOnlyList<TargetDto> targets, IReadOnlyList<SystemResolver> found,
        bool watchActive, IReadOnlySet<string> retired)
    {
        var retiredNow = new HashSet<string>(retired, StringComparer.OrdinalIgnoreCase);
        var result = targets.ToList();
        var log = new List<string>();
        var changed = false;

        var foundByAddress = found
            .GroupBy(f => f.Address.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Present addresses: create, or re-enable what we retired.
        foreach (var (address, resolver) in foundByAddress)
        {
            var index = result.FindIndex(t => t.SystemManaged && string.Equals(t.Address, address, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                if (!watchActive)
                    continue;
                var name = UniqueName(NameFor(resolver), result);
                result.Add(new TargetDto
                {
                    Name = name, Address = address, Profile = ProfileName, Probe = "dns",
                    Scope = TargetScope.Resolver, SystemManaged = true, Enabled = true,
                });
                log.Add($"added {name}");
                changed = true;
            }
            else if (!result[index].Enabled && watchActive && retiredNow.Remove(address))
            {
                result[index] = result[index] with { Enabled = true };
                log.Add($"re-enabled {result[index].Name}");
                changed = true;
            }
        }

        // Managed targets whose address is gone, or the watch is off: retire.
        for (var i = 0; i < result.Count; i++)
        {
            var t = result[i];
            if (!t.SystemManaged || !t.Enabled)
                continue;
            if (watchActive && foundByAddress.ContainsKey(t.Address))
                continue;
            result[i] = t with { Enabled = false };
            retiredNow.Add(t.Address);
            log.Add($"retired {t.Name}");
            changed = true;
        }

        return new Plan(result, retiredNow, changed, log);
    }

    private static string UniqueName(string wanted, List<TargetDto> targets)
    {
        if (!targets.Any(t => t.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            return wanted;
        for (var n = 2; ; n++)
        {
            var candidate = $"{wanted} ({n})";
            if (!targets.Any(t => t.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~SystemResolverReconcilerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/Dns/SystemResolverReconciler.cs Tests/ConWatch.Tests/SystemResolverReconcilerTests.cs
git commit -m "feat(dns): pure reconciler — create/retire/restore system resolver targets, admin disables stick"
```

---

### Task 10: The hosted service, DI, and the host test

**Files:**
- Create: `Programs/ConWatch/Dns/SystemResolverService.cs`
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs` (`AddConWatchEngine`, `AddConWatchHostedServices`)
- Create: `Tests/ConWatch.Tests/FakeSystemResolverSource.cs`
- Modify: `Tests/ConWatch.Tests/ConWatchFactory.cs`
- Test: `Tests/ConWatch.Tests/SystemResolverServiceTests.cs`

**Interfaces:**
- Produces: `SystemResolverService : BackgroundService` with `static readonly TimeSpan Interval = 5 min`, `Task<bool> ReconcileNowAsync(CancellationToken)` (returns whether the config changed; test hook and the service's own loop body). Registered as a hosted service **after** `WatcherHostService`, so the config is seeded and the `Resolver` profile exists before the first reconcile.
- `ConWatchFactory.Resolvers : FakeSystemResolverSource` (empty by default, so every existing host test is unaffected).

- [ ] **Step 1: Add the fake source and the factory hook**

Create `Tests/ConWatch.Tests/FakeSystemResolverSource.cs`:

```csharp
using System.Net;
using ConInfrastructure.Dns;

namespace ConWatch.Tests;

/// <summary>The OS resolver list, scripted. Empty unless a test says otherwise.</summary>
public sealed class FakeSystemResolverSource : ISystemResolverSource
{
    private readonly Lock _lock = new();
    private List<SystemResolver> _found = [];

    public void Set(params string[] addresses)
    {
        lock (_lock)
            _found = addresses.Select(a => new SystemResolver(IPAddress.Parse(a), a == "127.0.0.11")).ToList();
    }

    public IReadOnlyList<SystemResolver> Read()
    {
        lock (_lock)
            return _found.ToList();
    }
}
```

In `ConWatchFactory`: add `public FakeSystemResolverSource Resolvers { get; } = new();` and in `ConfigureServices`:

```csharp
            services.RemoveAll<ConInfrastructure.Dns.ISystemResolverSource>();
            services.AddSingleton<ConInfrastructure.Dns.ISystemResolverSource>(Resolvers);
```

- [ ] **Step 2: Write the failing host test**

Create `Tests/ConWatch.Tests/SystemResolverServiceTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Settings;
using ConWatch.Dns;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>Spec 2026-10-10 §4.3, D7: the host keeps one Resolver target per OS resolver.</summary>
public class SystemResolverServiceTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public SystemResolverServiceTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private SystemResolverService Service => _factory.Services.GetRequiredService<SystemResolverService>();

    private async Task<List<TargetDto>> TargetsAsync() =>
        (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!;

    private async Task<TargetDto?> ManagedAsync(string address) =>
        (await TargetsAsync()).SingleOrDefault(t => t.SystemManaged && t.Address == address);

    private async Task PutSettingAsync(string key, string value) =>
        (await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?> { [key] = value }))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task The_OS_resolver_becomes_a_managed_target_that_the_admin_can_only_disable()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");

        Assert.True(await Service.ReconcileNowAsync(CancellationToken.None));
        var managed = await ManagedAsync("10.0.0.53");
        Assert.NotNull(managed);
        Assert.Equal("Your resolver 10.0.0.53", managed.Name);
        Assert.Equal("Resolver", managed.Profile);
        Assert.Equal("dns", managed.Probe);
        Assert.Equal(TargetScope.Resolver, managed.Scope);
        Assert.True(managed.Enabled);

        // Steady state: no change, no second target.
        Assert.False(await Service.ReconcileNowAsync(CancellationToken.None));
        Assert.Single(await TargetsAsync(), t => t.SystemManaged);

        // The engine probes it with the dns spec and reports it in status.
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/status");
            return status.GetProperty("targets").EnumerateArray()
                .Any(t => t.GetProperty("systemManaged").GetBoolean() && t.GetProperty("probe").GetString() == "dns");
        });
        Assert.Contains(_factory.Pings.Created, c => c.Ip.Equals(IPAddress.Parse("10.0.0.53")) && c.Probe.Kind == ConCore.ProbeKind.Dns);

        // Delete is refused; re-addressing is refused; disabling works.
        var delete = await _client.DeleteAsync($"/api/v1/targets/{managed.Id}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        var move = await _client.PutAsJsonAsync($"/api/v1/targets/{managed.Id}?addressChange=correction", managed with { Address = "10.0.0.54" });
        Assert.Equal(HttpStatusCode.BadRequest, move.StatusCode);
        (await _client.PutAsJsonAsync($"/api/v1/targets/{managed.Id}", managed with { Enabled = false })).EnsureSuccessStatusCode();
        Assert.False((await ManagedAsync("10.0.0.53"))!.Enabled);

        // The admin's disable sticks across reconciles.
        await Service.ReconcileNowAsync(CancellationToken.None);
        Assert.False((await ManagedAsync("10.0.0.53"))!.Enabled);
    }

    [Fact]
    public async Task A_vanished_resolver_is_retired_and_restored_when_it_returns()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53", "10.0.0.54");
        await Service.ReconcileNowAsync(CancellationToken.None);

        _factory.Resolvers.Set("10.0.0.53");
        Assert.True(await Service.ReconcileNowAsync(CancellationToken.None));
        Assert.False((await ManagedAsync("10.0.0.54"))!.Enabled);
        Assert.True((await ManagedAsync("10.0.0.53"))!.Enabled);
        var settings = _factory.Services.GetRequiredService<SettingsStore>();
        Assert.Contains("10.0.0.54", await settings.GetAsync(SettingKeys.DnsRetiredResolvers) ?? "");

        _factory.Resolvers.Set("10.0.0.53", "10.0.0.54");
        Assert.True(await Service.ReconcileNowAsync(CancellationToken.None));
        Assert.True((await ManagedAsync("10.0.0.54"))!.Enabled);
        Assert.DoesNotContain("10.0.0.54", await settings.GetAsync(SettingKeys.DnsRetiredResolvers) ?? "");
    }

    [Fact]
    public async Task Turning_the_watch_off_retires_the_managed_targets_and_on_restores_them()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        _factory.Resolvers.Set("10.0.0.53");
        await Service.ReconcileNowAsync(CancellationToken.None);

        await PutSettingAsync(SettingKeys.DnsEnabled, "false");
        await ConfigApiTests.Eventually(async () => (await ManagedAsync("10.0.0.53"))?.Enabled == false);

        await PutSettingAsync(SettingKeys.DnsEnabled, "true");
        await ConfigApiTests.Eventually(async () => (await ManagedAsync("10.0.0.53"))?.Enabled == true);
    }

    [Fact]
    public async Task Nothing_is_created_before_the_config_is_seeded_or_when_nothing_is_found()
    {
        // The factory's default source is empty: the home seed stays exactly what the test wrote.
        await ConfigApiTests.SeedHomeAsync(_client);
        Assert.False(await Service.ReconcileNowAsync(CancellationToken.None));
        Assert.DoesNotContain(await TargetsAsync(), t => t.SystemManaged);
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~SystemResolverServiceTests"`
Expected: FAIL (compile: `SystemResolverService` missing).

- [ ] **Step 4: Implement the service and register it**

`Programs/ConWatch/Dns/SystemResolverService.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using ConCore.Dns;
using ConCore.Targets;
using ConInfrastructure.Dns;
using ConPersistence.Config;
using ConPersistence.Settings;

namespace ConWatch.Dns;

/// <summary>
/// Keeps one system-managed Resolver target per resolver the host is
/// configured to use (spec 2026-10-10 §4.3, D7). Reconciles at start, every
/// <see cref="Interval"/>, when the OS network addresses change and when a
/// Dns.* setting changes. Every change goes through ConfigService, which
/// rebuilds the engine the way any config edit does. Hosted AFTER
/// WatcherHostService so the config is seeded and the Resolver profile exists.
/// </summary>
public sealed class SystemResolverService(ISystemResolverSource source, ConfigService config,
    ConfigStore store, SettingsStore settings, TimeProvider time, ILogger<SystemResolverService> log)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _once = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        settings.SettingsChanged += OnSettingsChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileNowAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "System resolver reconcile failed; will retry");
                }
                var delay = Task.Delay(Interval, time, stoppingToken);
                var wake = _wake.Reader.ReadAsync(stoppingToken).AsTask();
                try { await Task.WhenAny(delay, wake); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            settings.SettingsChanged -= OnSettingsChanged;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _wake.Writer.TryWrite(true);

    private void OnSettingsChanged(IReadOnlyDictionary<string, string?> written)
    {
        if (written.Keys.Any(k => k is SettingKeys.DnsEnabled or SettingKeys.DnsLookupNames))
            _wake.Writer.TryWrite(true);
    }

    /// <summary>One reconcile, serialized. True when the stored config changed.</summary>
    public async Task<bool> ReconcileNowAsync(CancellationToken ct)
    {
        await _once.WaitAsync(ct);
        try
        {
            if (await store.IsEmptyAsync())
                return false; // first boot, before WatcherHostService seeds

            var found = source.Read();
            var enabled = await settings.GetAsync(SettingKeys.DnsEnabled) != "false";
            var names = DnsLookupName.FromJson(await settings.GetAsync(SettingKeys.DnsLookupNames));
            var retired = Parse(await settings.GetAsync(SettingKeys.DnsRetiredResolvers));

            SystemResolverReconciler.Plan? plan = null;
            var result = await config.EditDocumentAsync(doc =>
            {
                // Active: the watch is on and there is something to ask for — the
                // configured names, or any hostname target the resolver must resolve anyway.
                var active = enabled && (names.Count > 0 || doc.Targets.Any(t =>
                    t.Enabled && t.Scope != TargetScope.Resolver && !IPAddress.TryParse(t.Address, out _)));
                plan = SystemResolverReconciler.Reconcile(doc.Targets, found, active, retired);
                if (!plan.Changed)
                    return ConfigResult.Fail(ConfigErrorKind.None, "no change"); // commits nothing
                doc.Targets.Clear();
                doc.Targets.AddRange(plan.Targets);
                return ConfigResult.Ok();
            });

            if (plan is null || !plan.Changed)
                return false;
            if (!result.Success)
            {
                log.LogWarning("System resolver reconcile rejected: {Errors}", string.Join("; ", result.Errors ?? []));
                return false;
            }
            if (!plan.Retired.SetEquals(retired))
                await settings.SetManyAsync(new Dictionary<string, string?>
                {
                    [SettingKeys.DnsRetiredResolvers] = plan.Retired.Count == 0 ? null : string.Join('\n', plan.Retired.Order()),
                });
            foreach (var line in plan.Log)
                log.LogInformation("System resolvers: {Change}", line);
            return true;
        }
        finally
        {
            _once.Release();
        }
    }

    private static HashSet<string> Parse(string? value) =>
        new((value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}
```



`ConWatchServices.AddConWatchEngine`: add

```csharp
        services.AddSingleton<ConInfrastructure.Dns.ISystemResolverSource, ConInfrastructure.Dns.SystemResolverSource>();
        services.AddSingleton<Dns.SystemResolverService>();
```

`AddConWatchHostedServices`: immediately after the `WatcherHostService` hosted registration:

```csharp
        // After WatcherHostService: the config is seeded and the Resolver profile exists.
        services.AddHostedService(sp => sp.GetRequiredService<Dns.SystemResolverService>());
```

- [ ] **Step 5: Run the host tests**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~SystemResolverServiceTests"` then the whole `dotnet test Tests/ConWatch.Tests`
Expected: PASS. The default fake source is empty, so no other host test sees a managed target.

- [ ] **Step 6: Commit**

```bash
git add Programs/ConWatch/Dns/SystemResolverService.cs Programs/ConWatch/Startup/ConWatchServices.cs Tests/ConWatch.Tests/FakeSystemResolverSource.cs Tests/ConWatch.Tests/ConWatchFactory.cs Tests/ConWatch.Tests/SystemResolverServiceTests.cs
git commit -m "feat(dns): hosted system-resolver service — reconciles the OS resolver list into config at start, every 5 min, on network and Dns.* changes"
```

---

### Task 11: `DnsResultLog`

**Files:**
- Create: `Core/ConEngine/Dns/DnsResultLog.cs`
- Modify: `Core/ConEngine/WatcherEngine.cs` (property, pump)
- Test: `Tests/ConEngine.Tests/DnsResultLogTests.cs`

**Interfaces:**
- Produces: `DnsResult(DateTimeOffset At, bool Success, string Message)`; `DnsResultLog` with `const int Keep = 20`, `void Record(Guid targetId, PingResult result)`, `IReadOnlyList<DnsResult> Recent(Guid targetId)` (oldest first, empty for unknown ids); `WatcherEngine.DnsResults : DnsResultLog` fed with every Resolver-scope sample. Later plans' notes (`ResolverLying`, `NameUnresolvable`) and the status `lookupNames[]` read it.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/DnsResultLogTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Dns;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class DnsResultLogTests
{
    [Fact]
    public void Keeps_the_last_twenty_per_target_oldest_first()
    {
        var log = new DnsResultLog();
        var id = Guid.NewGuid();
        var ip = IPAddress.Loopback;
        Assert.Empty(log.Recent(id));

        for (var i = 0; i < 25; i++)
            log.Record(id, new PingResult(ip, i % 2 == 0, 5, $"m{i}", DateTimeOffset.UnixEpoch.AddSeconds(i)));

        var recent = log.Recent(id);
        Assert.Equal(DnsResultLog.Keep, recent.Count);
        Assert.Equal("m5", recent[0].Message);
        Assert.Equal("m24", recent[^1].Message);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(24), recent[^1].At);
        Assert.True(recent[^1].Success);
        Assert.Empty(log.Recent(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_engine_logs_Resolver_samples_and_nothing_else()
    {
        var options = new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig
                {
                    Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Internal",
                    Probe = "dns", Scope = TargetScope.Resolver,
                },
            ],
        };
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            var resolverId = engine.Graph.GetByName("Your resolver 203.0.113.1").Id;
            var routerId = engine.Graph.GetByName("Router").Id;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (engine.DnsResults.Recent(resolverId).Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Assert.NotEmpty(engine.DnsResults.Recent(resolverId));
            Assert.Empty(engine.DnsResults.Recent(routerId));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsResultLogTests"`
Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`Core/ConEngine/Dns/DnsResultLog.cs`:

```csharp
using ConCore;

namespace ConEngine.Dns;

/// <summary>One DNS probe outcome, as the transceiver classified it.</summary>
public sealed record DnsResult(DateTimeOffset At, bool Success, string Message);

/// <summary>
/// The last <see cref="Keep"/> DNS results per Resolver target, in memory and
/// per engine like the loss windows (spec 2026-10-10 §4.2). The notes
/// (ResolverLying, NameUnresolvable) and the status lookupNames[] read it.
/// </summary>
public sealed class DnsResultLog
{
    public const int Keep = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Queue<DnsResult>> _results = new();

    public void Record(Guid targetId, PingResult result)
    {
        lock (_lock)
        {
            if (!_results.TryGetValue(targetId, out var queue))
                _results[targetId] = queue = new Queue<DnsResult>(Keep);
            queue.Enqueue(new DnsResult(result.Timestamp, result.Success, result.Message));
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
}
```

`WatcherEngine.cs`: add the property after `Loss`:

```csharp
    /// <summary>The last DNS results per Resolver target (spec 2026-10-10 §4.2). Exists from construction.</summary>
    public ConEngine.Dns.DnsResultLog DnsResults { get; } = new();
```

and in the pump, right after the `Scope == Verdict` block:

```csharp
                    else if (monitor.Target.Scope == TargetScope.Resolver)
                        DnsResults.Record(monitor.Target.Id, result);
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsResultLogTests|FullyQualifiedName~ResolverScopeEngineTests|FullyQualifiedName~WatcherEngineTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/Dns/DnsResultLog.cs Core/ConEngine/WatcherEngine.cs Tests/ConEngine.Tests/DnsResultLogTests.cs
git commit -m "feat(dns): DnsResultLog — last 20 DNS outcomes per Resolver target, fed by the engine pump"
```

---

### Task 12: Keep resolvers out of the tree and the clone; README

**Files:**
- Modify: `Programs/ConWatch/Pages/Config/Targets.cshtml:70,108`
- Modify: `Programs/ConWatch/Pages/Index.cshtml.cs:57-63`
- Modify: `Programs/ConWatch/ClientApp/src/lib/lan.ts`, `src/lib/types.ts:16,71`, `src/dashboard.tsx:124`
- Modify: `Programs/ConWatch/Clone/CloneService.cs:55-65,140-152,238-252`
- Modify: `README.md:186-187`
- Test: `Programs/ConWatch/ClientApp/src/lib/lan.test.ts`, `Tests/ConWatch.Tests/CloneWithoutSystemManagedTests.cs`

**Interfaces:**
- Produces: `isResolver(t)` in `lan.ts`; `CloneService.WithoutSystemManaged(Snapshot) : Snapshot` applied unconditionally on both clone paths (system-managed targets, their transitions, rollups and glitches are dropped; the destination regenerates them from its own OS).

- [ ] **Step 1: Write the failing client test**

Append to `Programs/ConWatch/ClientApp/src/lib/lan.test.ts`:

```ts
describe("isResolver", () => {
  it("is true only for scope Resolver", () => {
    expect(isResolver(t({ scope: "Resolver" }))).toBe(true);
    expect(isResolver(t({ scope: "Local" }))).toBe(false);
    expect(isResolver(t({}))).toBe(false);
  });

  it("a resolver is not a LAN device", () => {
    expect(isLocal(t({ scope: "Resolver" }))).toBe(false);
    expect(summarizeLan([t({ scope: "Resolver" })]).total).toBe(0);
  });
});
```

and extend the import to `import { isLocal, isResolver, summarizeLan } from "./lan";`.

- [ ] **Step 2: Run it to verify it fails**

Run (in `Programs/ConWatch/ClientApp`): `npm test -- --run src/lib/lan.test.ts`
Expected: FAIL (`isResolver` is not exported).

- [ ] **Step 3: Implement the client side**

`lan.ts`, after `isLocal`:

```ts
/** Resolver targets are DNS resolvers (spec 2026-10-10): never in the tree, never LAN devices. */
export const isResolver = (t: { scope?: string }): boolean => t.scope === "Resolver";
```

`types.ts`: both `scope?: "Verdict" | "Local";` become `scope?: "Verdict" | "Local" | "Resolver";`, and add `systemManaged?: boolean;` beside `critical` on `TargetStatus`.

`dashboard.tsx`: import `isResolver` with `isLocal`, and the tree becomes

```tsx
      <Tree topology={topology} targets={state.targets.filter((t) => !isLocal(t) && !isResolver(t))} pools={state.pools}
```

- [ ] **Step 4: Run the client tests**

Run: `npm test -- --run`
Expected: PASS.

- [ ] **Step 5: Server-side listings**

`Targets.cshtml` line 70: `@foreach (var t in Model.Config.Targets.Where(t => t.Scope != TargetScope.Resolver).OrderBy(t => t.Name))`; line 108: `.Where(t => t.Scope == TargetScope.Verdict)` (a parent must be a Verdict target; this also keeps Local out, as before).

`Index.cshtml.cs`, after the `Local` branch in the monitors loop:

```csharp
                if (monitor.Target.Scope == TargetScope.Resolver)
                    continue; // name-resolution section comes with a later plan; never a tree root
```

- [ ] **Step 6: Write the failing clone test**

Create `Tests/ConWatch.Tests/CloneWithoutSystemManagedTests.cs`:

```csharp
using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Snapshots;
using ConWatch.Clone;

namespace ConWatch.Tests;

/// <summary>Spec 2026-10-10 §5.1: a clone never carries the source's system
/// resolvers — the destination regenerates them from its own OS.</summary>
public class CloneWithoutSystemManagedTests
{
    private static readonly Guid ManagedId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid UpstreamId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly DateTime Stamp = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static Snapshot Build() => new()
    {
        Config = new ConfigDocument
        {
            Targets =
            {
                new TargetDto
                {
                    Id = ManagedId, Name = "Your resolver 10.1.10.1", Address = "10.1.10.1", Profile = "Resolver",
                    Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true,
                },
                new TargetDto
                {
                    Id = UpstreamId, Name = "1.1.1.1 directly", Address = "1.1.1.1", Profile = "Resolver",
                    Probe = "dns", Scope = TargetScope.Resolver,
                },
            },
        },
        Transitions =
        {
            new TransitionRow { TimestampUtc = Stamp, SubjectId = ManagedId, SubjectName = "Your resolver 10.1.10.1" },
            new TransitionRow { TimestampUtc = Stamp, SubjectId = UpstreamId, SubjectName = "1.1.1.1 directly" },
        },
        Rollups =
        {
            new RollupRow { TargetId = ManagedId, PeriodStartUtc = Stamp },
            new RollupRow { TargetId = UpstreamId, PeriodStartUtc = Stamp },
        },
        Glitches =
        {
            new GlitchRow { TargetId = ManagedId, TargetName = "Your resolver 10.1.10.1", StartUtc = Stamp, EndUtc = Stamp },
        },
    };

    [Fact]
    public void System_managed_targets_and_their_rows_are_dropped_upstream_resolvers_stay()
    {
        var before = Build();
        var after = CloneService.WithoutSystemManaged(before);

        Assert.Equal([UpstreamId], after.Config.Targets.Select(t => t.Id!.Value));
        Assert.Equal([UpstreamId], after.Transitions.Select(t => t.SubjectId));
        Assert.Equal([UpstreamId], after.Rollups.Select(r => r.TargetId));
        Assert.Empty(after.Glitches);

        Assert.Equal(2, before.Config.Targets.Count); // input not mutated
    }
}
```

- [ ] **Step 7: Run it to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~CloneWithoutSystemManagedTests"`
Expected: FAIL (compile).

- [ ] **Step 8: Implement the clone filter**

In `CloneService.cs`, beside `WithoutLocalDevices`:

```csharp
    /// <summary>System resolvers are the SOURCE host's; the destination's
    /// SystemResolverService regenerates its own (spec 2026-10-10 §5.1).</summary>
    internal static Snapshot WithoutSystemManaged(Snapshot snapshot)
    {
        var managedIds = snapshot.Config.Targets
            .Where(t => t.SystemManaged && t.Id is not null)
            .Select(t => t.Id!.Value).ToHashSet();
        return snapshot with
        {
            Config = snapshot.Config with
            {
                Targets = snapshot.Config.Targets.Where(t => !t.SystemManaged).ToList(),
            },
            Transitions = snapshot.Transitions.Where(t => !managedIds.Contains(t.SubjectId)).ToList(),
            Rollups = snapshot.Rollups.Where(r => !managedIds.Contains(r.TargetId)).ToList(),
            Glitches = snapshot.Glitches.Where(g => !managedIds.Contains(g.TargetId)).ToList(),
        };
    }
```

(Match `WithoutLocalDevices`'s exact `with` shape, including how it filters `Glitches`, so both filters agree.)

Apply it unconditionally on both paths, right after `var snapshot = fetched.Snapshot!;` (clone, line ~57) and after the same line in the take-over path (line ~141):

```csharp
        snapshot = WithoutSystemManaged(snapshot);
```

- [ ] **Step 9: README**

Replace the privacy bullet at `README.md:186-187` with:

```markdown
- ICMP pings, TCP connects and DNS lookups for the targets **you** configured
  (that's the product), plus the name-resolution watch's DNS queries: it asks
  the resolvers your machine is configured with for the lookup names under
  Config → DNS (seeded with `dns.google`, `one.one.one.one`, `dns.quad9.net`
  and `dns.opendns.com`; edit or empty the list, or turn the watch off, and
  those queries stop);
```

- [ ] **Step 10: Run everything**

Run: `dotnet test ConnectionWatcher.sln` and, in `Programs/ConWatch/ClientApp`, `npm test -- --run`
Expected: PASS across the board.

- [ ] **Step 11: Commit**

```bash
git add Programs/ConWatch/Pages/Config/Targets.cshtml Programs/ConWatch/Pages/Index.cshtml.cs Programs/ConWatch/ClientApp/src/lib/lan.ts Programs/ConWatch/ClientApp/src/lib/lan.test.ts Programs/ConWatch/ClientApp/src/lib/types.ts Programs/ConWatch/ClientApp/src/dashboard.tsx Programs/ConWatch/Clone/CloneService.cs Tests/ConWatch.Tests/CloneWithoutSystemManagedTests.cs README.md
git commit -m "feat(dns): resolvers stay out of the tree, the Targets table and clones; README privacy list names the lookup queries"
```

---

## Self-review notes

- **Spec coverage (step 1 of the rollout):** §4.1 model → Tasks 1–4 (scope, probe, flag, profile; `DnsOptions` is realised as the two settings keys in Task 5, which the spec's §4.1 bullet should say — a one-line spec edit accompanies this plan). §4.2 probe → Tasks 5–7, 11. §4.3 OS list and reconcile → Tasks 8–10 (host-side service rather than the engine, because the engine is rebuilt from stored config on every change; the spec's §4.3 wording is corrected alongside). §5.1 clone rule → Task 12. §5.5 upgrade → Task 4's migration plus Task 5's `SeedMissingAsync` defaults and Task 10's first reconcile. The verdict, notes, `dns` status block, config panel, wizard, tray, webhooks and MQTT are later plans.
- **Type consistency:** `DnsLookupName.Expected` is `IReadOnlyList<string>` everywhere; `IDnsLookupNames.Next(IPAddress)`; `SystemResolver.Address` is `IPAddress` and the reconciler keys on `Address.ToString()`; `StatusTarget` gains `SystemManaged` as its last positional parameter; `ConfigResult.Errors` is the error list (verified).
- **Review Focus:** 1 → Task 7 stray-id test; 2 → Task 8 throwing source and Task 9 empty list; 3 → Task 9 suffix test; 4 → Task 9 admin-disabled test and Task 10 host test; 5 → Task 6 truncated test.
