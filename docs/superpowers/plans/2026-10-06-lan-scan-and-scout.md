# LAN Scan, Tray Scouting and Host-Network Docs Implementation Plan (Plan 3 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish the Discoveries feature: a **LAN scan** (ARP table + ping sweep of the machine's own subnet, with MAC vendor names) that runs on the server from the Discoveries page and on a Windows PC from the tray, a **tray scout** that posts what it finds to the server, **MCP access that matches the web UI** (open when no admin password is set, admin-only when one is), the deferred **hardening** from plan 2, and **host-network docs** for NAS containers.

**Architecture:** One scanner library (`Libraries/ConInfrastructure/Discovery`) grows a `LanScanner` behind the existing `IDiscoveryScanner`; the server's `DiscoverySearchService` runs it with the same gate/30 s cap but keeps **partial** results. A `ScoutClient` (also in `ConInfrastructure`, fully tested against the in-memory server) does the tray's HTTP work — post, log in on a 401, map failures to plain messages, honour the plain-HTTP password guard — so the WinForms tray stays thin. `TransportPolicy` moves into `ConInfrastructure` so the tray and the server share it.

**Tech Stack:** .NET 10 (server, libraries, WinForms tray `net10.0-windows`), ASP.NET Razor Pages, EF Core + SQLite, ModelContextProtocol 2.2.0, PowerShell (one tool script), xunit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-10-02-discoveries-and-local-scope-design.md` (§2 searches incl. the LAN scan and the tray, §4 endpoints and MCP, §5 errors, §6 testing). Plans 1A, 1B and 2 are merged on `master`: `TargetScope`, the Local UI, the Discoveries table/store/REST/page, the router and network-edge searches, the read-only MCP `list_discoveries`.

**Decisions made by the user for this plan (2026-10-06):**
1. **MCP parity with the web UI:** `list_discoveries` works when **no admin password is set** and is **admin-only when one is** — exactly what the Discoveries page does. (Replaces plan 2's "always open on MCP".)
2. **Vendor names:** a script downloads the public IEEE OUI registry **once** and a compact table is committed; no network access at runtime.

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` enabled; no `TreatWarningsAsErrors`.
- New tests use RFC 5737 addresses (`192.0.2.x`, `198.51.100.x`, `203.0.113.x`) for *discovery data* and RFC 1918 addresses (`192.168.x.x`) only inside LAN-scanner fakes (the scanner is private-range-only by design). Tests wait on conditions, never sleep-then-assert (CI runners have 2 cores).
- **The LAN scan must never sweep a non-private range**: only RFC 1918 (`10/8`, `172.16/12`, `192.168/16`), at most the /24 around the machine's own address per interface, at most 1024 hosts per scan, concurrency ≤ 64, per-host timeout 1 s (spec §2). A test pins each limit.
- All discovery REST routes stay on the **locked** group; `ApiSurfaceLockTests` must pass unchanged (do not touch its allowlist).
- Discoveries stay **node-local**: never exported, cloned or snapshotted.
- Config pages use inline vanilla JS and `wwwroot/lib/site-config.js` helpers; no Vite, no new web dependencies. The tray may add exactly one NuGet package (`System.Security.Cryptography.ProtectedData`) for DPAPI.
- Enums cross JSON as strings; every inbound enum is checked with `Enum.IsDefined`.
- Migration command (if ever needed — this plan adds no table): `dotnet ef migrations add <Name> --project Libraries/ConPersistence`.
- Test commands per project (never the whole solution): `dotnet test Tests/ConPersistence.Tests -c Debug`, `Tests/ConEngine.Tests` (also hosts the `ConInfrastructure` tests), `Tests/ConWatch.Tests`, and in the final task `ConConfig.Tests`, `ConWatchCli.Tests`, `ConNotify.Tests`; filter with `--filter "FullyQualifiedName~<Class>"`. `ConWatch.Tests` has one known cold-start flake in `ProbeFieldTests` (passes on rerun). The tray has **no test project** (CI skips it); build it with `dotnet build Programs/ConWatchTray -c Debug` on Windows.
- Commit after every task. **Never push** unless the user says so. Commit messages carry no attribution lines. Work on a feature branch in a git worktree (e.g. `lan-scan-and-scout`), not on `master`. Do not edit `README.md` (the user has an uncommitted rewrite); docs in this plan go in `deploy/docker/` and `SECURITY.md`.
- Privacy: real scans on the developer's machine return real MACs/hostnames/addresses. They must never be written into committed files, test data or reports — say "N devices" only.

## Review Focus

Failure modes the spec implies that no task's main tests would catch; each has a test in the owning task:

1. **Scan scope:** a `/16` or `/8` interface sweeps only the /24 around the machine; a public (or CGNAT `100.64/10`, or link-local `169.254/16`) interface is never swept; a `/30` or `/31` doesn't underflow (Task 4).
2. **Our own address and the default gateways** never appear as LocalDevice results (the gateway already has its own kind) (Task 4).
3. **Cancellation / the 30 s cap keeps partial results** — they are stored, reported as `partial`, and the gate is released (Tasks 4, 5).
4. **Garbage from `arp -a` / `/proc/net/arp`** (empty text, header only, localized words, "(incomplete)" lines, broadcast/multicast MACs) never throws and never yields a bogus device (Task 2).
5. **A randomized (locally administered) MAC** is labelled as such, not given a wrong vendor (Task 3).
6. **The tray never sends the admin password over plain HTTP to a non-loopback host without an explicit yes**, never logs or shows it, binds a stored password to the server URL it was entered for, and offers a way to forget it (Tasks 8, 9).
7. **MCP parity:** with an admin password set and no admin cookie `list_discoveries` is a clear error; with no password, or with the admin cookie, it works (Task 7).
8. **Hostile/odd addresses** to `POST /discoveries` — `"1"`, `"1.2.3"`, `0.x.x.x`, IPv4-mapped IPv6 multicast, scoped IPv6 — are rejected, and concurrent identical upserts end as one row (Task 1).

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs` | Address hardening | 1 |
| `Libraries/ConInfrastructure/Discovery/NeighborTable.cs` (new) | ARP/neighbor parsers + system reader | 2 |
| `tools/Update-OuiTable.ps1` (new), `Libraries/ConInfrastructure/Discovery/oui.tsv` (generated), `OuiTable.cs` (new), `ConInfrastructure.csproj` | MAC vendor lookup | 3 |
| `Libraries/ConInfrastructure/Discovery/LanScanner.cs` (new), `DiscoveryScanner.cs` | The LAN scan; `IDiscoveryScanner.ScanLanAsync` | 4 |
| `Programs/ConWatch/Discoveries/DiscoverySearchService.cs`, `Api/DiscoveryEndpoints.cs`, `Startup/ConWatchServices.cs`, `Pages/Config/Discoveries.cshtml` | Server LAN search, partial results, page button | 5 |
| `Programs/ConWatch/Pages/Config/Discoveries.cshtml.cs` | Network-edge adopt prefills its parent | 6 |
| `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`, `Startup/ConWatchServices.cs`, `SECURITY.md` | MCP parity with the admin gate | 7 |
| `Libraries/ConInfrastructure/Net/TransportPolicy.cs` (moved), `Discovery/ScoutClient.cs` (new), `Programs/ConWatch/Clone/CloneService.cs` | Scout HTTP client; shared transport policy | 8 |
| `Programs/ConWatchTray/*` | Tray menu, scouting, password prompt/store | 9 |
| `deploy/docker/compose.yaml`, `deploy/docker/README.md` (new) | Host-network docs | 10 |

New test files: `Tests/ConEngine.Tests/NeighborTableTests.cs`, `OuiTableTests.cs`, `LanScannerTests.cs`; `Tests/ConWatch.Tests/ScoutClientTests.cs`. Existing tests extended where named.

---

### Task 1: Harden address handling and pin the upsert's concurrency

**Files:**
- Modify: `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs`
- Test: modify `Tests/ConPersistence.Tests/DiscoveryStoreTests.cs`, `Tests/ConWatch.Tests/DiscoveryApiTests.cs`

**Interfaces:**
- Consumes: `DiscoveryStore.Validate` / `UpsertAsync` (plan 2).
- Produces: `TryNormalizeAddress` additionally rejects: text without a full dotted quad or an IPv6 colon (`"1"`, `"1.2.3"`); the whole `0.0.0.0/8`; scoped IPv6 (`fe80::1%eth0`, any `ScopeId != 0`); and IPv4-mapped IPv6 is unmapped to IPv4 **before** the range checks (so `::ffff:224.0.0.1` is rejected and `::ffff:192.0.2.9` is stored as `192.0.2.9`).

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ConPersistence.Tests/DiscoveryStoreTests.cs` (inside the class):

```csharp
    [Theory]
    [InlineData("1")]
    [InlineData("1.2.3")]
    [InlineData("0.1.2.3")]
    [InlineData("0.0.0.255")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::ffff:224.0.0.1")]
    [InlineData("ff02::1")]
    [InlineData("::1")]
    [InlineData("::")]
    public async Task More_unusable_addresses_are_rejected(string address)
    {
        var result = await _store.UpsertAsync([Router(address)]);
        Assert.False(result.Success);
        Assert.Empty(await _store.ListAsync(null));
    }

    [Fact]
    public async Task IPv6_is_stored_canonically_and_an_IPv4_mapped_address_is_unmapped()
    {
        await _store.UpsertAsync([Router("2001:DB8::10"), Router("::ffff:192.0.2.9")]);

        Assert.Equal(["192.0.2.9", "2001:db8::10"], (await _store.ListAsync(null)).Select(d => d.Address).Order());

        var again = await _store.UpsertAsync([Router("  2001:0db8:0000:0000:0000:0000:0000:0010 ")]);
        Assert.Equal((0, 1), (again.Created, again.Updated));        // same row, not a second one
        Assert.Equal(2, (await _store.ListAsync(null)).Count);
    }

    [Fact]
    public void A_null_item_is_a_validation_error_not_an_exception()
    {
        var errors = DiscoveryStore.Validate([null!]);
        Assert.Single(errors);
    }

    [Fact]
    public async Task Whitespace_around_an_address_still_hits_the_same_row()
    {
        await _store.UpsertAsync([Router("192.0.2.1")]);
        var result = await _store.UpsertAsync([Router(" 192.0.2.1 ")]);
        Assert.Equal((0, 1), (result.Created, result.Updated));
    }

    [Fact]
    public async Task A_rescan_without_a_hostname_keeps_the_hostname_it_already_had()
    {
        await _store.UpsertAsync([new("192.0.2.50", "nas.example.com", DiscoveryKind.LocalDevice,
            DiscoveryMethod.ArpTable, "server", null)]);
        await _store.UpsertAsync([new("192.0.2.50", null, DiscoveryKind.LocalDevice,
            DiscoveryMethod.PingSweep, "server", null)]);

        Assert.Equal("nas.example.com", (await _store.ListAsync(null)).Single().Hostname);
    }
```

Add to `Tests/ConWatch.Tests/DiscoveryApiTests.cs` (inside the class) the concurrency pin — it needs the real file-backed database, which this class's `ConWatchFactory` provides (the unit-test in-memory factory shares one connection and cannot run transactions in parallel):

```csharp
    [Fact]
    public async Task Concurrent_identical_posts_end_as_one_row_with_no_failures()
    {
        var body = new
        {
            items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } },
        };

        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => _client.PostAsJsonAsync("/api/v1/discoveries", body)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var counts = await Task.WhenAll(responses.Select(async r =>
            JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync())));
        Assert.Equal(1, counts.Sum(c => c.GetProperty("created").GetInt32()));
        Assert.Equal(11, counts.Sum(c => c.GetProperty("updated").GetInt32()));
        Assert.Single(Items(await GetJson("/api/v1/discoveries?status=all")));
    }
```

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~DiscoveryStoreTests"`
Expected: the new rejection cases and the mapped-IPv6 test FAIL (`"1"` is accepted as `0.0.0.1`, `0.x` and mapped multicast pass, scoped IPv6 keeps its scope); the null-item, whitespace, hostname and concurrency tests may already PASS — they pin existing behaviour. If the concurrency test fails, that is a real defect: report it instead of retrying.

- [ ] **Step 2: Implement**

In `DiscoveryStore.cs` replace `TryNormalizeAddress` with:

```csharp
    private static bool TryNormalizeAddress(string? text, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.Trim();
        // IPAddress.TryParse accepts legacy short forms ("1" is 0.0.0.1, "1.2.3" is 1.2.0.3):
        // demand a full dotted quad or an IPv6 literal.
        if (!trimmed.Contains(':') && trimmed.Count(c => c == '.') != 3)
            return false;
        // A scope id ("fe80::1%eth0") names a local interface; it means nothing to another node.
        if (trimmed.Contains('%') || !IPAddress.TryParse(trimmed, out var ip) || ip.ScopeId != 0)
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            ip.Equals(IPAddress.Broadcast) || ip.Equals(IPAddress.None))
            return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var first = ip.GetAddressBytes()[0];
            if (first == 0 || first >= 224)           // 0.0.0.0/8 "this network"; multicast and reserved
                return false;
        }
        else if (ip.IsIPv6Multicast)
        {
            return false;
        }
        normalized = ip.ToString();
        return true;
    }
```

- [ ] **Step 3: Run and commit**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug` and `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~DiscoveryApiTests"`
Expected: all PASS.

```bash
git add Libraries Tests
git commit -m "feat(discoveries): reject short-form, 0/8, scoped and mapped-multicast addresses; pin upsert concurrency"
```

---

### Task 2: ARP / neighbor table reader

**Files:**
- Create: `Libraries/ConInfrastructure/Discovery/NeighborTable.cs`
- Test: create `Tests/ConEngine.Tests/NeighborTableTests.cs`

**Interfaces:**
- Produces: `NeighborEntry(string Address, string Mac)` (Mac normalized to lowercase `aa:bb:cc:dd:ee:ff`); `INeighborTable { IReadOnlyList<NeighborEntry> Read(); }`; `NeighborParser.ParseProcNetArp(string)` and `NeighborParser.ParseArpCommand(string)` (both pure, never throw, skip incomplete/broadcast/multicast/zero MACs); `SystemNeighborTable` (Linux `/proc/net/arp`, otherwise `arp -a` on Windows / `arp -an` elsewhere, 5 s cap; any failure → empty list).

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/NeighborTableTests.cs`:

```csharp
using ConInfrastructure.Discovery;

namespace ConEngine.Tests;

public class NeighborTableTests
{
    private const string Linux = """
        IP address       HW type     Flags       HW address            Mask     Device
        192.168.1.1      0x1         0x2         aa:bb:cc:00:11:22     *        eth0
        192.168.1.20     0x1         0x2         AA:BB:CC:00:11:33     *        eth0
        192.168.1.99     0x1         0x0         00:00:00:00:00:00     *        eth0
        192.168.1.77     0x1         0x2         ff:ff:ff:ff:ff:ff     *        eth0
        """;

    private const string Windows = """

        Interface: 192.168.1.50 --- 0x5
          Internet Address      Physical Address      Type
          192.168.1.1           aa-bb-cc-00-11-22     dynamic
          192.168.1.20          aa-bb-cc-00-11-33     dynamic
          192.168.1.255         ff-ff-ff-ff-ff-ff     static
          224.0.0.22            01-00-5e-00-00-16     static
          239.255.255.250       01-00-5e-7f-ff-fa     static
        """;

    private const string Mac = """
        ? (192.168.1.1) at aa:bb:cc:0:11:22 on en0 ifscope [ethernet]
        ? (192.168.1.20) at (incomplete) on en0 ifscope [ethernet]
        ? (192.168.1.30) at a:b:c:d:e:f on en0 ifscope [ethernet]
        """;

    [Fact]
    public void Linux_proc_net_arp_yields_complete_unicast_entries_only()
    {
        var entries = NeighborParser.ParseProcNetArp(Linux);
        Assert.Equal(
            [new NeighborEntry("192.168.1.1", "aa:bb:cc:00:11:22"), new NeighborEntry("192.168.1.20", "aa:bb:cc:00:11:33")],
            entries);
    }

    [Fact]
    public void Windows_arp_a_skips_broadcast_and_multicast_and_normalizes_dashes()
    {
        var entries = NeighborParser.ParseArpCommand(Windows);
        Assert.Equal(
            [new NeighborEntry("192.168.1.1", "aa:bb:cc:00:11:22"), new NeighborEntry("192.168.1.20", "aa:bb:cc:00:11:33")],
            entries);
    }

    [Fact]
    public void Mac_arp_an_pads_short_octets_and_skips_incomplete_entries()
    {
        var entries = NeighborParser.ParseArpCommand(Mac);
        Assert.Equal(
            [new NeighborEntry("192.168.1.1", "aa:bb:cc:00:11:22"), new NeighborEntry("192.168.1.30", "0a:0b:0c:0d:0e:0f")],
            entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("IP address HW type Flags HW address Mask Device")]
    [InlineData("garbage garbage garbage garbage")]
    [InlineData("999.999.999.999 aa-bb-cc-dd-ee-ff dynamic")]
    [InlineData("Interface: 192.168.1.50 --- 0x5\n  Internet Address      Physical Address      Type\n")]
    [InlineData("192.168.1.1 gg-hh-ii-jj-kk-ll dynamic")]
    public void Garbage_never_throws_and_yields_nothing(string text)
    {
        Assert.Empty(NeighborParser.ParseProcNetArp(text));
        Assert.Empty(NeighborParser.ParseArpCommand(text));
    }

    [Fact]
    public void The_system_reader_never_throws_whatever_the_machine_has()
    {
        var entries = new SystemNeighborTable().Read();
        Assert.NotNull(entries);
        Assert.All(entries, e => Assert.Matches("^([0-9a-f]{2}:){5}[0-9a-f]{2}$", e.Mac));
    }
}
```

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~NeighborTableTests"`
Expected: build FAIL — the types do not exist.

- [ ] **Step 2: Implement**

Create `Libraries/ConInfrastructure/Discovery/NeighborTable.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace ConInfrastructure.Discovery;

/// <summary>An IP the machine has recently talked to on its LAN, with its MAC (lowercase, colon-separated).</summary>
public sealed record NeighborEntry(string Address, string Mac);

public interface INeighborTable
{
    IReadOnlyList<NeighborEntry> Read();
}

/// <summary>Pure parsers for the OS neighbor tables. They never throw: unparseable lines are skipped.</summary>
public static partial class NeighborParser
{
    [GeneratedRegex(@"\b(\d{1,3}(?:\.\d{1,3}){3})\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:\-])([0-9A-Fa-f]{1,2})([:\-])([0-9A-Fa-f]{1,2})(?:\2[0-9A-Fa-f]{1,2}){4}(?![0-9A-Fa-f:\-])")]
    private static partial Regex MacAddress();

    /// <summary>Linux <c>/proc/net/arp</c>: header, then <c>IP  HWtype  Flags  HWaddr  Mask  Device</c>; flags 0x0 is an incomplete entry.</summary>
    public static IReadOnlyList<NeighborEntry> ParseProcNetArp(string? text)
    {
        var entries = new List<NeighborEntry>();
        foreach (var line in (text ?? "").Split('\n').Skip(1))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[2] == "0x0")
                continue;
            if (IsIpv4(parts[0]) && TryNormalizeMac(parts[3], out var mac))
                entries.Add(new NeighborEntry(parts[0], mac));
        }
        return entries;
    }

    /// <summary>Windows <c>arp -a</c> and macOS <c>arp -an</c>: an IPv4 and a MAC on the same line.</summary>
    public static IReadOnlyList<NeighborEntry> ParseArpCommand(string? text)
    {
        var entries = new List<NeighborEntry>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var ip = Ipv4().Match(line);
            var macMatch = MacAddress().Match(line);
            if (!ip.Success || !macMatch.Success || !IsIpv4(ip.Groups[1].Value))
                continue;
            if (TryNormalizeMac(macMatch.Value, out var mac))
                entries.Add(new NeighborEntry(ip.Groups[1].Value, mac));
        }
        return entries;
    }

    private static bool IsIpv4(string text) =>
        IPAddress.TryParse(text, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && text.Count(c => c == '.') == 3;

    /// <summary>Normalizes "AA-BB-..", "a:b:c:d:e:f" (macOS drops leading zeros) to "aa:bb:..";
    /// false for malformed, all-zero, broadcast and multicast MACs.</summary>
    internal static bool TryNormalizeMac(string text, out string mac)
    {
        mac = "";
        var octets = text.Split(':', '-');
        if (octets.Length != 6)
            return false;
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (octets[i].Length is < 1 or > 2 || !byte.TryParse(octets[i], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }
        if (bytes.All(b => b == 0) || bytes.All(b => b == 0xff) || (bytes[0] & 1) == 1)   // zero, broadcast, multicast
            return false;
        mac = string.Join(':', bytes.Select(b => b.ToString("x2")));
        return true;
    }
}

/// <summary>The machine's own neighbor table. Any failure — no <c>arp</c>, no permission, a hang — is an empty list:
/// the ping sweep's results still stand without it.</summary>
public sealed class SystemNeighborTable : INeighborTable
{
    public IReadOnlyList<NeighborEntry> Read()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/net/arp"))
                return NeighborParser.ParseProcNetArp(File.ReadAllText("/proc/net/arp"));

            var start = new ProcessStartInfo("arp", OperatingSystem.IsWindows() ? "-a" : "-an")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                return [];
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return [];
            }
            return NeighborParser.ParseArpCommand(output.GetAwaiter().GetResult());
        }
        catch (Exception)
        {
            return [];
        }
    }
}
```

- [ ] **Step 3: Run and commit**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~NeighborTableTests"` then the full `Tests/ConEngine.Tests`
Expected: all PASS.

```bash
git add Libraries Tests
git commit -m "feat(discoveries): ARP/neighbor table reader with defensive parsers"
```

---

### Task 3: MAC vendor lookup from a one-time-downloaded IEEE table

**Files:**
- Create: `tools/Update-OuiTable.ps1`, `Libraries/ConInfrastructure/Discovery/OuiTable.cs`, `Libraries/ConInfrastructure/Discovery/oui.tsv` (generated by the script)
- Modify: `Libraries/ConInfrastructure/ConInfrastructure.csproj`
- Test: create `Tests/ConEngine.Tests/OuiTableTests.cs`

**Interfaces:**
- Produces: `IVendorLookup { string? Describe(string mac); }` — returns a vendor name, `"private (randomized) address"` for a locally administered MAC, or `null` when unknown; `OuiTable : IVendorLookup` with `OuiTable.Default` (lazily loads the embedded resource) and a test constructor `OuiTable(IEnumerable<string> tsvLines)`; the embedded resource is `oui.tsv`, lines `AABBCC<TAB>Vendor name` (upper-case hex, one OUI per line).
- **User-approved external download:** `tools/Update-OuiTable.ps1` fetches the public IEEE MA-L registry (`https://standards-oui.ieee.org/oui/oui.csv`, a few MB) **once**; the generated `oui.tsv` is committed. No runtime network access.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/OuiTableTests.cs`:

```csharp
using ConInfrastructure.Discovery;

namespace ConEngine.Tests;

public class OuiTableTests
{
    private static readonly OuiTable Small = new(["AABBCC\tExample Networks", "001122\tOther Vendor"]);

    [Theory]
    [InlineData("aa:bb:cc:00:11:22", "Example Networks")]
    [InlineData("AA-BB-CC-DD-EE-FF", "Example Networks")]
    [InlineData("00:11:22:33:44:55", "Other Vendor")]
    public void Known_prefixes_return_the_vendor_whatever_the_separator_or_case(string mac, string vendor) =>
        Assert.Equal(vendor, Small.Describe(mac));

    [Fact]
    public void An_unknown_prefix_is_null()
        => Assert.Null(Small.Describe("10:20:30:40:50:60"));

    [Theory]
    [InlineData("02:00:00:00:00:01")]
    [InlineData("06:aa:bb:cc:dd:ee")]
    [InlineData("0a:aa:bb:cc:dd:ee")]
    [InlineData("0e:aa:bb:cc:dd:ee")]
    [InlineData("fa:aa:bb:cc:dd:ee")]
    public void A_locally_administered_mac_is_labelled_randomized_not_given_a_vendor(string mac)
    {
        // Even if its prefix collided with a registry entry, a private address says nothing about the maker.
        var table = new OuiTable(["020000\tCollision Corp", "06AABB\tCollision Corp", "0AAABB\tCollision Corp"]);
        Assert.Equal("private (randomized) address", table.Describe(mac));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a mac")]
    [InlineData("aa:bb:cc")]
    public void A_malformed_mac_is_null_not_an_exception(string mac) => Assert.Null(Small.Describe(mac));

    [Fact]
    public void The_embedded_registry_is_present_and_large_and_knows_a_stable_entry()
    {
        var table = OuiTable.Default;
        Assert.True(table.Count > 10_000, $"only {table.Count} entries — was the table generated?");
        Assert.Contains("Cisco", table.Describe("00:00:0c:12:34:56") ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
```

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~OuiTableTests"`
Expected: build FAIL.

- [ ] **Step 2: The generator script**

Create `tools/Update-OuiTable.ps1`:

```powershell
<#
.SYNOPSIS
  Regenerates Libraries/ConInfrastructure/Discovery/oui.tsv from the public IEEE MA-L registry.
.DESCRIPTION
  Downloads https://standards-oui.ieee.org/oui/oui.csv (public data, a few MB) and writes a compact
  "AABBCC<TAB>Vendor" file, one line per OUI, sorted. Run it by hand when you want fresher vendor
  names; the result is committed and embedded in the binary — the app never touches the network for this.
#>
[CmdletBinding()]
param(
    [string]$Url = 'https://standards-oui.ieee.org/oui/oui.csv',
    [string]$Out = (Join-Path $PSScriptRoot '..\Libraries\ConInfrastructure\Discovery\oui.tsv')
)
$ErrorActionPreference = 'Stop'

$temp = Join-Path ([IO.Path]::GetTempPath()) "oui-$([Guid]::NewGuid()).csv"
try {
    Invoke-WebRequest -Uri $Url -OutFile $temp -UseBasicParsing
    $rows = Import-Csv -Path $temp     # Registry, Assignment, Organization Name, Organization Address
    $lines = foreach ($row in $rows) {
        $oui = ($row.Assignment ?? '').Trim().ToUpperInvariant()
        if ($oui -notmatch '^[0-9A-F]{6}$') { continue }
        $name = ($row.'Organization Name' ?? '').Trim() -replace '\s+', ' '
        if ($name.Length -eq 0) { continue }
        if ($name.Length -gt 48) { $name = $name.Substring(0, 48).TrimEnd() }
        "$oui`t$name"
    }
    $sorted = $lines | Sort-Object -Unique
    [IO.File]::WriteAllLines($Out, $sorted, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Wrote $($sorted.Count) entries to $Out"
}
finally {
    Remove-Item -Path $temp -ErrorAction SilentlyContinue
}
```

Run it once from the repo root: `pwsh -File tools/Update-OuiTable.ps1` (PowerShell 7). Expected: "Wrote N entries" with N in the tens of thousands. Check that `Libraries/ConInfrastructure/Discovery/oui.tsv` exists, is UTF-8 without BOM, and that `grep -c . oui.tsv` equals N. If the download fails (network, proxy), report BLOCKED with the exact error — do not hand-write a table.

- [ ] **Step 3: The lookup and the embedded resource**

`Libraries/ConInfrastructure/ConInfrastructure.csproj` — add inside an `<ItemGroup>`:

```xml
    <EmbeddedResource Include="Discovery\oui.tsv" LogicalName="oui.tsv" />
```

Create `Libraries/ConInfrastructure/Discovery/OuiTable.cs`:

```csharp
using System.Reflection;

namespace ConInfrastructure.Discovery;

public interface IVendorLookup
{
    /// <summary>A vendor name; "private (randomized) address" for a locally administered MAC
    /// (phones and laptops rotate these; the prefix says nothing about the maker); null when unknown.</summary>
    string? Describe(string mac);
}

/// <summary>The IEEE OUI registry, embedded as oui.tsv ("AABBCC&lt;TAB&gt;Vendor"). Generated by tools/Update-OuiTable.ps1.</summary>
public sealed class OuiTable : IVendorLookup
{
    public const string Randomized = "private (randomized) address";

    private static readonly Lazy<OuiTable> DefaultTable = new(LoadEmbedded);
    private readonly Dictionary<string, string> _vendors;

    public static OuiTable Default => DefaultTable.Value;

    public OuiTable(IEnumerable<string> tsvLines)
    {
        _vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in tsvLines)
        {
            var tab = line.IndexOf('\t');
            if (tab == 6)
                _vendors[line[..6]] = line[(tab + 1)..].Trim();
        }
    }

    public int Count => _vendors.Count;

    public string? Describe(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12 || (mac ?? "").Any(c => !Uri.IsHexDigit(c) && c is not (':' or '-')))
            return null;
        // Locally administered bit (0x02 of the first octet): randomized/private, not a registered maker.
        if ((Convert.ToByte(hex[..2], 16) & 0x02) != 0)
            return Randomized;
        return _vendors.TryGetValue(hex[..6], out var vendor) ? vendor : null;
    }

    private static OuiTable LoadEmbedded()
    {
        using var stream = typeof(OuiTable).Assembly.GetManifestResourceStream("oui.tsv");
        if (stream is null)
            return new OuiTable([]);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return new OuiTable(lines);
    }
}
```

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~OuiTableTests"` then the full `Tests/ConEngine.Tests`
Expected: all PASS. Confirm the built `ConInfrastructure.dll` grew by roughly the table's size (about 1 MB).

```bash
git add tools Libraries Tests
git commit -m "feat(discoveries): MAC vendor lookup from an embedded IEEE OUI table (generated once by tools/Update-OuiTable.ps1)"
```

(State in the commit body that the table was generated from the public IEEE MA-L registry on the date it was run.)

---

### Task 4: The LAN scanner

**Files:**
- Create: `Libraries/ConInfrastructure/Discovery/LanScanner.cs`
- Modify: `Libraries/ConInfrastructure/Discovery/DiscoveryScanner.cs`
- Test: create `Tests/ConEngine.Tests/LanScannerTests.cs`; modify `Tests/ConEngine.Tests/DiscoveryScannerTests.cs` only where its constructor calls change

**Interfaces:**
- Consumes: `INeighborTable` (Task 2), `IVendorLookup` (Task 3), `DiscoveryCandidate`/`DiscoveryKind`/`DiscoveryMethod` (plan 2).
- Produces:
  - `LocalNetwork(IPAddress Address, int PrefixLength, IReadOnlyList<IPAddress> Gateways)`; `ILocalNetworks { IReadOnlyList<LocalNetwork> Read(); }`; `SystemLocalNetworks` (up, non-loopback interfaces' IPv4 unicast addresses with their prefix and gateways).
  - `LanScanResult(IReadOnlyList<DiscoveryCandidate> Found, bool Complete)`.
  - `LanScanner(ILocalNetworks, INeighborTable, IVendorLookup, Func<IPAddress, CancellationToken, Task<long?>> ping, Func<IPAddress, CancellationToken, Task<string?>> reverseDns, int concurrency = 64)` with `Task<LanScanResult> ScanAsync(CancellationToken ct)`; static `LanScanner.HostsToSweep(IReadOnlyList<LocalNetwork>)`, `LanScanner.IcmpPing`, `LanScanner.ReverseDns`; constants `MaxHosts = 1024`, `PingTimeout = 1 s`.
  - `IDiscoveryScanner.ScanLanAsync(CancellationToken ct)` → `Task<LanScanResult>`; `DiscoveryScanner` takes a `LanScanner` as a third constructor parameter.
- Rules (spec §2, Review Focus 1-3): only RFC 1918 networks; per interface at most the /24 around the machine's address (a /25–/30 sweeps just its hosts, a /31 or /32 nothing); never our own address, the network or broadcast address, or a default gateway; cancellation returns what was found with `Complete = false`; ARP entries count only when inside the swept hosts.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/LanScannerTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using ConCore.Discovery;
using ConInfrastructure.Discovery;

namespace ConEngine.Tests;

public class LanScannerTests
{
    private sealed class Networks(params LocalNetwork[] nets) : ILocalNetworks
    {
        public IReadOnlyList<LocalNetwork> Read() => nets;
    }

    private sealed class Arp(params NeighborEntry[] entries) : INeighborTable
    {
        public IReadOnlyList<NeighborEntry> Read() => entries;
    }

    private sealed class Vendors : IVendorLookup
    {
        public string? Describe(string mac) => mac.StartsWith("aa:bb:cc") ? "Example Networks"
            : mac.StartsWith("02:") ? OuiTable.Randomized : null;
    }

    private static LocalNetwork Net(string address, int prefix, params string[] gateways) =>
        new(IPAddress.Parse(address), prefix, gateways.Select(IPAddress.Parse).ToList());

    private sealed class Probe
    {
        public ConcurrentBag<string> Pinged { get; } = new();
        public Dictionary<string, long> Answers { get; init; } = new();

        public Task<long?> Ping(IPAddress ip, CancellationToken ct)
        {
            Pinged.Add(ip.ToString());
            return Task.FromResult(Answers.TryGetValue(ip.ToString(), out var rtt) ? rtt : (long?)null);
        }
    }

    private static LanScanner Scanner(ILocalNetworks nets, INeighborTable arp, Probe probe,
        Func<IPAddress, CancellationToken, Task<string?>>? dns = null, int concurrency = 64) =>
        new(nets, arp, new Vendors(), probe.Ping, dns ?? ((_, _) => Task.FromResult<string?>(null)), concurrency);

    [Fact]
    public async Task A_sweep_reports_pingers_and_ARP_only_devices_with_mac_vendor_and_evidence()
    {
        var probe = new Probe { Answers = { ["192.168.1.20"] = 3, ["192.168.1.21"] = 7 } };
        var arp = new Arp(
            new NeighborEntry("192.168.1.20", "aa:bb:cc:00:11:33"),     // answered ping AND in ARP
            new NeighborEntry("192.168.1.30", "02:11:22:33:44:55"),     // ARP only, randomized MAC
            new NeighborEntry("192.168.1.1", "aa:bb:cc:00:11:22"),      // the gateway: excluded
            new NeighborEntry("192.168.1.50", "aa:bb:cc:00:11:44"),     // our own address: excluded
            new NeighborEntry("10.9.9.9", "aa:bb:cc:00:11:55"));        // outside the swept hosts: ignored
        var scanner = Scanner(new Networks(Net("192.168.1.50", 24, "192.168.1.1")), arp, probe);

        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(["192.168.1.20", "192.168.1.21", "192.168.1.30"], result.Found.Select(c => c.Address));
        Assert.All(result.Found, c => Assert.Equal(DiscoveryKind.LocalDevice, c.Kind));

        var both = result.Found[0];
        Assert.Equal(DiscoveryMethod.ArpTable, both.Method);
        Assert.Contains("aa:bb:cc:00:11:33", both.Evidence);
        Assert.Contains("Example Networks", both.Evidence);
        Assert.Contains("3 ms", both.Evidence);

        var pingOnly = result.Found[1];
        Assert.Equal(DiscoveryMethod.PingSweep, pingOnly.Method);
        Assert.Contains("7 ms", pingOnly.Evidence);
        Assert.DoesNotContain("MAC", pingOnly.Evidence);

        var arpOnly = result.Found[2];
        Assert.Equal(DiscoveryMethod.ArpTable, arpOnly.Method);
        Assert.Contains("private (randomized) address", arpOnly.Evidence);
        Assert.Contains("no ping reply", arpOnly.Evidence);
    }

    [Fact]
    public async Task Hostnames_come_from_reverse_dns_and_a_failing_lookup_is_just_no_name()
    {
        var probe = new Probe { Answers = { ["192.168.1.20"] = 3, ["192.168.1.21"] = 4 } };
        var scanner = Scanner(new Networks(Net("192.168.1.50", 24)), new Arp(), probe,
            dns: (ip, _) => ip.ToString() == "192.168.1.20"
                ? Task.FromResult<string?>("nas.example.com")
                : throw new InvalidOperationException("dns exploded"));

        var found = (await scanner.ScanAsync(CancellationToken.None)).Found;

        Assert.Equal("nas.example.com", found.Single(c => c.Address == "192.168.1.20").Hostname);
        Assert.Null(found.Single(c => c.Address == "192.168.1.21").Hostname);
    }

    [Fact]
    public async Task A_slash_24_sweeps_253_hosts_never_our_own_the_network_or_the_broadcast_address()
    {
        var probe = new Probe();
        await Scanner(new Networks(Net("192.168.1.50", 24)), new Arp(), probe).ScanAsync(CancellationToken.None);

        Assert.Equal(253, probe.Pinged.Count);
        Assert.DoesNotContain("192.168.1.50", probe.Pinged);
        Assert.DoesNotContain("192.168.1.0", probe.Pinged);
        Assert.DoesNotContain("192.168.1.255", probe.Pinged);
    }

    [Theory]
    [InlineData("192.168.7.50", 16, "192.168.7.")]      // a /16 sweeps only the /24 around us
    [InlineData("10.20.30.40", 8, "10.20.30.")]         // so does a /8
    [InlineData("172.16.5.5", 20, "172.16.5.")]
    public async Task A_wide_network_sweeps_only_the_slash_24_around_the_machine(string address, int prefix, string expected)
    {
        var probe = new Probe();
        await Scanner(new Networks(Net(address, prefix)), new Arp(), probe).ScanAsync(CancellationToken.None);

        Assert.Equal(253, probe.Pinged.Count);
        Assert.All(probe.Pinged, p => Assert.StartsWith(expected, p));
    }

    [Theory]
    [InlineData("192.168.1.130", 25, 125, "192.168.1.")]    // 128..255 -> 129..254 minus ourselves
    [InlineData("192.168.1.9", 28, 13, "192.168.1.")]       // 0..15 -> 1..14 minus ourselves
    [InlineData("192.168.1.5", 30, 1, "192.168.1.")]        // 4..7 -> 5,6 minus ourselves
    [InlineData("192.168.1.5", 31, 0, "")]
    [InlineData("192.168.1.5", 32, 0, "")]
    public async Task Narrower_networks_sweep_just_their_own_hosts_and_tiny_ones_nothing(
        string address, int prefix, int count, string expectedPrefix)
    {
        var probe = new Probe();
        await Scanner(new Networks(Net(address, prefix)), new Arp(), probe).ScanAsync(CancellationToken.None);

        Assert.Equal(count, probe.Pinged.Count);
        Assert.All(probe.Pinged, p => Assert.StartsWith(expectedPrefix, p));
    }

    [Theory]
    [InlineData("203.0.113.5", 24)]      // public
    [InlineData("100.64.5.5", 24)]       // carrier-grade NAT
    [InlineData("169.254.3.3", 16)]      // link-local (an unconfigured interface)
    [InlineData("8.8.8.8", 24)]
    [InlineData("127.0.0.1", 8)]
    public async Task A_non_private_network_is_never_swept(string address, int prefix)
    {
        var probe = new Probe();
        var result = await Scanner(new Networks(Net(address, prefix)), new Arp(), probe)
            .ScanAsync(CancellationToken.None);

        Assert.Empty(probe.Pinged);
        Assert.Empty(result.Found);
        Assert.True(result.Complete);
    }

    [Fact]
    public async Task Several_interfaces_are_all_swept_but_the_total_is_capped_and_duplicates_collapse()
    {
        var probe = new Probe();
        var nets = Enumerable.Range(1, 8).Select(i => Net($"10.0.{i}.5", 24)).ToList();
        nets.Add(Net("10.0.1.77", 24));            // a second address on the same /24: swept once

        await Scanner(new Networks(nets.ToArray()), new Arp(), probe).ScanAsync(CancellationToken.None);

        Assert.Equal(probe.Pinged.Count, probe.Pinged.Distinct().Count());
        Assert.True(probe.Pinged.Count <= LanScanner.MaxHosts);
        Assert.True(probe.Pinged.Count >= 800);    // eight /24s ~ 2000 hosts, capped at 1024
    }

    [Fact]
    public async Task Concurrency_never_exceeds_the_limit()
    {
        var current = 0;
        var peak = 0;
        var scanner = new LanScanner(new Networks(Net("192.168.1.50", 24)), new Arp(), new Vendors(),
            async (_, _) =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, now);
                await Task.Yield();
                Interlocked.Decrement(ref current);
                return null;
            },
            (_, _) => Task.FromResult<string?>(null), concurrency: 8);

        await scanner.ScanAsync(CancellationToken.None);

        Assert.InRange(peak, 1, 8);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                break;
    }

    [Fact]
    public async Task Cancellation_returns_what_was_found_so_far_and_says_it_is_incomplete()
    {
        using var cts = new CancellationTokenSource();
        var answered = 0;
        var scanner = new LanScanner(new Networks(Net("192.168.1.50", 24)),
            new Arp(new NeighborEntry("192.168.1.30", "aa:bb:cc:00:11:99")), new Vendors(),
            async (ip, ct) =>
            {
                if (Interlocked.Increment(ref answered) > 5)
                    await Task.Delay(Timeout.Infinite, ct);   // the cap fires while these hang
                return 2;
            },
            (_, _) => Task.FromResult<string?>(null), concurrency: 8);

        var scan = scanner.ScanAsync(cts.Token);
        await Task.Delay(200);                                  // let the first five answers land
        cts.Cancel();
        var result = await scan.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Complete);
        Assert.True(result.Found.Count >= 5, $"only {result.Found.Count} kept");
        Assert.Contains(result.Found, c => c.Address == "192.168.1.30");   // the ARP table is still read
    }

    [Fact]
    public async Task No_networks_means_an_empty_complete_result()
    {
        var result = await Scanner(new Networks(), new Arp(), new Probe()).ScanAsync(CancellationToken.None);
        Assert.Empty(result.Found);
        Assert.True(result.Complete);
    }

    [Fact]
    public void HostsToSweep_is_exposed_for_pinning_and_agrees_with_the_rules()
    {
        var hosts = LanScanner.HostsToSweep([Net("192.168.1.50", 24)]).Select(h => h.ToString()).ToList();
        Assert.Equal(254, hosts.Count);            // own address filtering happens in ScanAsync
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);
    }
}
```

(The `await Task.Delay(200)` in the cancellation test waits for a *condition that cannot be observed otherwise* — the fake pinger has no signal to hang a wait on. If it proves flaky, replace it with a `TaskCompletionSource` the fake sets when `answered` reaches 6, and `await` that instead — that is the preferred form; do it.)

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~LanScannerTests"`
Expected: build FAIL — `LanScanner`, `ILocalNetworks`, `LocalNetwork` do not exist.

- [ ] **Step 2: Implement**

Create `Libraries/ConInfrastructure/Discovery/LanScanner.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ConCore.Discovery;

namespace ConInfrastructure.Discovery;

/// <summary>An IPv4 address on one of this machine's up interfaces, with its subnet prefix and gateways.</summary>
public sealed record LocalNetwork(IPAddress Address, int PrefixLength, IReadOnlyList<IPAddress> Gateways);

public interface ILocalNetworks
{
    IReadOnlyList<LocalNetwork> Read();
}

public sealed class SystemLocalNetworks : ILocalNetworks
{
    public IReadOnlyList<LocalNetwork> Read()
    {
        var result = new List<LocalNetwork>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var properties = nic.GetIPProperties();
                var gateways = properties.GatewayAddresses
                    .Select(g => g.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
                foreach (var unicast in properties.UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        result.Add(new LocalNetwork(unicast.Address, unicast.PrefixLength, gateways));
            }
        }
        catch (NetworkInformationException)
        {
            // No interface information: nothing to scan.
        }
        return result;
    }
}

public sealed record LanScanResult(IReadOnlyList<DiscoveryCandidate> Found, bool Complete);

/// <summary>
/// The LAN scan (spec §2): ping-sweep the machine's own private subnet, then read the neighbor
/// table, so devices that ignore pings but answered ARP still show up. Private ranges only, at
/// most the /24 around each interface address, at most <see cref="MaxHosts"/> hosts, bounded
/// concurrency. Cancellation keeps what was found.
/// </summary>
public sealed class LanScanner(ILocalNetworks networks, INeighborTable neighbors, IVendorLookup vendors,
    Func<IPAddress, CancellationToken, Task<long?>> ping,
    Func<IPAddress, CancellationToken, Task<string?>> reverseDns, int concurrency = 64)
{
    public const int MaxHosts = 1024;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);
    private const int DnsParallelism = 16;

    public async Task<LanScanResult> ScanAsync(CancellationToken ct)
    {
        var nets = networks.Read().Where(n => IsRfc1918(n.Address)).ToList();
        var own = nets.Select(n => n.Address.ToString()).ToHashSet();
        var gateways = nets.SelectMany(n => n.Gateways).Select(g => g.ToString()).ToHashSet();
        var hosts = HostsToSweep(nets).Where(h => !own.Contains(h.ToString())).Take(MaxHosts).ToList();
        var swept = hosts.Select(h => h.ToString()).ToHashSet();

        var answered = new ConcurrentDictionary<string, long>();
        var complete = true;
        try
        {
            await Parallel.ForEachAsync(hosts,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
                async (host, token) =>
                {
                    if (await ping(host, token).ConfigureAwait(false) is { } rtt)
                        answered[host.ToString()] = rtt;
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            complete = false;
        }

        // The sweep fills the ARP cache; read it even after a cancel — it is quick and local.
        var arp = new Dictionary<string, string>();
        foreach (var entry in neighbors.Read())
            if (swept.Contains(entry.Address))
                arp.TryAdd(entry.Address, entry.Mac);

        var addresses = answered.Keys.Union(arp.Keys)
            .Where(a => !own.Contains(a) && !gateways.Contains(a))
            .OrderBy(a => IpToUInt(IPAddress.Parse(a)))
            .ToList();

        var names = await ResolveNamesAsync(addresses, complete ? ct : CancellationToken.None).ConfigureAwait(false);
        var found = addresses.Select(a =>
        {
            arp.TryGetValue(a, out var mac);
            var hasPing = answered.TryGetValue(a, out var rtt);
            var parts = new List<string>();
            if (mac is not null)
            {
                var vendor = vendors.Describe(mac);
                parts.Add(vendor is null ? $"MAC {mac}" : $"MAC {mac} ({vendor})");
            }
            parts.Add(hasPing ? $"answers pings in {rtt} ms" : "in the ARP table, no ping reply");
            return new DiscoveryCandidate(a, names.GetValueOrDefault(a), DiscoveryKind.LocalDevice,
                mac is not null ? DiscoveryMethod.ArpTable : DiscoveryMethod.PingSweep,
                string.Join("; ", parts));
        }).ToList();
        return new LanScanResult(found, complete);
    }

    private async Task<Dictionary<string, string>> ResolveNamesAsync(List<string> addresses, CancellationToken ct)
    {
        var names = new ConcurrentDictionary<string, string>();
        try
        {
            await Parallel.ForEachAsync(addresses,
                new ParallelOptions { MaxDegreeOfParallelism = DnsParallelism, CancellationToken = ct },
                async (address, token) =>
                {
                    try
                    {
                        if (await reverseDns(IPAddress.Parse(address), token).ConfigureAwait(false) is { Length: > 0 } name)
                            names[address] = name;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A failed lookup is just "no name".
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Names are a nicety; keep what resolved.
        }
        return new Dictionary<string, string>(names);
    }

    /// <summary>
    /// The candidate hosts for these networks: per network the hosts of its subnet, but at most the
    /// /24 around the machine's address (a /16 is never swept whole); a /31 or /32 has none. RFC 1918
    /// only is enforced by the caller. The machine's own address is NOT removed here.
    /// </summary>
    public static IEnumerable<IPAddress> HostsToSweep(IReadOnlyList<LocalNetwork> nets)
    {
        var seen = new HashSet<uint>();
        foreach (var net in nets)
        {
            var prefix = Math.Max(net.PrefixLength, 24);
            if (prefix >= 31)
                continue;
            var address = IpToUInt(net.Address);
            var mask = uint.MaxValue << (32 - prefix);
            var network = address & mask;
            var broadcast = network | ~mask;
            for (var host = network + 1; host < broadcast; host++)
                if (seen.Add(host))
                    yield return UIntToIp(host);
        }
    }

    public static bool IsRfc1918(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static uint IpToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress UIntToIp(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    /// <summary>One ICMP echo with <see cref="PingTimeout"/>; null when there is no reply.</summary>
    public static async Task<long?> IcmpPing(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(address, PingTimeout, cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }

    /// <summary>Reverse DNS with a 1 s cap; null when there is no name.</summary>
    public static async Task<string?> ReverseDns(IPAddress address, CancellationToken ct)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(address.ToString(), ct);
            var winner = await Task.WhenAny(lookup, Task.Delay(1000, ct)).ConfigureAwait(false);
            return winner == lookup ? (await lookup.ConfigureAwait(false)).HostName : null;
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return null;
        }
    }
}
```

`DiscoveryScanner.cs` — extend the interface and class:

```csharp
public interface IDiscoveryScanner
{
    Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct);
    Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct);

    /// <summary>Sweep the machine's own private subnet and read its neighbor table. A cancelled scan
    /// returns what it found with <see cref="LanScanResult.Complete"/> false.</summary>
    Task<LanScanResult> ScanLanAsync(CancellationToken ct);
}

public sealed class DiscoveryScanner(IGatewayDiscovery gateways, TracerouteRunner traceroute, LanScanner lan) : IDiscoveryScanner
```

and add `public Task<LanScanResult> ScanLanAsync(CancellationToken ct) => lan.ScanAsync(ct);`. Update the existing `DiscoveryScannerTests.Scanner(...)` helper to pass a `LanScanner` built from empty fakes (`new LanScanner(new EmptyNetworks(), new EmptyArp(), new NoVendors(), (_, _) => Task.FromResult<long?>(null), (_, _) => Task.FromResult<string?>(null))` — define the three tiny fakes in that file) so the existing tests keep compiling.

- [ ] **Step 3: Run and commit**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: all PASS. (`ConWatch` will not compile yet — `ConWatchServices` builds `DiscoveryScanner` with two arguments and the test fakes lack `ScanLanAsync`; Task 5 fixes both. If you want a green build for this commit, make the minimal `ConWatchServices` and test-fake edits described in Task 5 Step 2 now and move those hunks into this commit.)

```bash
git add Libraries Tests
git commit -m "feat(discoveries): LAN scanner — private-range ping sweep plus ARP table, vendor names, partial results on cancel"
```

---

### Task 5: Server-side LAN search, partial results, and the page button

**Files:**
- Modify: `Programs/ConWatch/Discoveries/DiscoverySearchService.cs`, `Programs/ConWatch/Api/DiscoveryEndpoints.cs`, `Programs/ConWatch/Startup/ConWatchServices.cs`, `Programs/ConWatch/Pages/Config/Discoveries.cshtml`
- Test: modify `Tests/ConWatch.Tests/DiscoveryApiTests.cs`, `Tests/ConWatch.Tests/DiscoverySearchServiceTests.cs`, `Tests/ConWatch.Tests/DiscoveriesPageTests.cs`

**Interfaces:**
- Consumes: `IDiscoveryScanner.ScanLanAsync`, `LanScanResult` (Task 4), `ContainerHint`.
- Produces: `SearchOutcome` gains a trailing `bool Partial = false`; `DiscoverySearchService.SearchLanAsync(ct)`; `POST /api/v1/discoveries/search/lan` (locked) → `200 { found, created, updated, partial, warning }` (409 `search-busy` when another search runs, as for the other searches); a **"Scan local network"** button (`id="find-lan"`) on the Discoveries page. A LAN search that runs past the 30 s cap keeps and stores what it found and reports `partial: true`; the router and edge searches keep their "nothing stored on timeout" behaviour (they have no partial results).

- [ ] **Step 1: Update the existing fakes and write the failing tests**

Every `IDiscoveryScanner` implementation in the test project must gain `ScanLanAsync`. In `DiscoveryApiTests.FakeScanner` add:

```csharp
        public static LanScanResult Lan { get; set; } = new(
            [new DiscoveryCandidate("192.168.1.20", "nas.example.com", DiscoveryKind.LocalDevice,
                DiscoveryMethod.ArpTable, "MAC aa:bb:cc:00:11:33; answers pings in 3 ms")], Complete: true);

        public Task<LanScanResult> ScanLanAsync(CancellationToken ct) => Task.FromResult(Lan);
```

(reset `Lan` to a fresh default in the test constructor next to `HoldEdge`). In `DiscoverySearchServiceTests` give `HangingScanner` and `ThrowingScanner` a `ScanLanAsync` (`Hang`-style / throwing) and add a third scanner:

```csharp
    private sealed class PartialScanner : IDiscoveryScanner
    {
        public Task<IReadOnlyList<DiscoveryCandidate>> FindRouterAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiscoveryCandidate>> FindNetworkEdgeAsync(string host, CancellationToken ct) => throw new NotSupportedException();

        /// <summary>Finds one device, then waits for the cap and returns an incomplete result — like the real sweep.</summary>
        public async Task<LanScanResult> ScanLanAsync(CancellationToken ct)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return new LanScanResult(
                [new DiscoveryCandidate("192.168.1.20", null, DiscoveryKind.LocalDevice, DiscoveryMethod.PingSweep, "answers pings in 2 ms")],
                Complete: false);
        }
    }
```

New tests in `DiscoverySearchServiceTests`:

```csharp
    [Fact]
    public async Task A_lan_scan_that_hits_the_cap_keeps_its_partial_results_and_says_so()
    {
        var store = new DiscoveryStore(_db);
        var service = new DiscoverySearchService(new PartialScanner(), store,
            NullLogger<DiscoverySearchService>.Instance, cap: TimeSpan.FromMilliseconds(100));

        var outcome = await service.SearchLanAsync(CancellationToken.None);

        Assert.True(outcome.Partial);
        Assert.False(outcome.TimedOut);
        Assert.Equal((1, 1, 0), (outcome.Found, outcome.Created, outcome.Updated));
        Assert.Single(await store.ListAsync(null));
        Assert.False((await service.SearchLanAsync(CancellationToken.None)).Busy);   // the gate was released
    }

    [Fact]
    public async Task A_client_disconnect_during_a_lan_scan_is_cancellation_not_a_partial_result()
    {
        var service = new DiscoverySearchService(new PartialScanner(), new DiscoveryStore(_db),
            NullLogger<DiscoverySearchService>.Instance);
        using var cts = new CancellationTokenSource();
        var search = service.SearchLanAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }
```

New tests in `DiscoveryApiTests`:

```csharp
    [Fact]
    public async Task Lan_search_stores_devices_as_LocalDevice_with_origin_server_and_a_rescan_updates()
    {
        var first = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/v1/discoveries/search/lan", null)).Content.ReadAsStringAsync());
        Assert.Equal(1, first.GetProperty("created").GetInt32());
        Assert.False(first.GetProperty("partial").GetBoolean());

        var row = Assert.Single(Items(await GetJson("/api/v1/discoveries")));
        Assert.Equal("LocalDevice", row.GetProperty("kind").GetString());
        Assert.Equal("ArpTable", row.GetProperty("method").GetString());
        Assert.Equal("server", row.GetProperty("origin").GetString());
        Assert.Equal("nas.example.com", row.GetProperty("hostname").GetString());
        Assert.Contains("aa:bb:cc:00:11:33", row.GetProperty("evidence").GetString());

        var second = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/v1/discoveries/search/lan", null)).Content.ReadAsStringAsync());
        Assert.Equal(0, second.GetProperty("created").GetInt32());
        Assert.Equal(1, second.GetProperty("updated").GetInt32());
    }

    [Fact]
    public async Task A_partial_lan_scan_is_stored_and_flagged_partial()
    {
        FakeScanner.Lan = new LanScanResult(FakeScanner.Lan.Found, Complete: false);

        var body = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/v1/discoveries/search/lan", null)).Content.ReadAsStringAsync());

        Assert.True(body.GetProperty("partial").GetBoolean());
        Assert.Equal(1, body.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task A_lan_scan_that_finds_nothing_says_so_plainly()
    {
        FakeScanner.Lan = new LanScanResult([], Complete: true);
        var body = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.PostAsync("/api/v1/discoveries/search/lan", null)).Content.ReadAsStringAsync());
        Assert.Equal(0, body.GetProperty("found").GetInt32());
        Assert.False(body.GetProperty("partial").GetBoolean());
    }
```

Also extend the existing lock test's route list with `("POST", "/api/v1/discoveries/search/lan")`, and add to `DiscoveriesPageTests`:

```csharp
    [Fact]
    public async Task The_page_offers_a_Scan_local_network_button()
    {
        var html = await _client.GetStringAsync("/Config/Discoveries");
        Assert.Contains("id=\"find-lan\"", html);
        Assert.Contains("Scan local network", html);
    }
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~DiscoveryApiTests|FullyQualifiedName~DiscoverySearchServiceTests|FullyQualifiedName~DiscoveriesPageTests"`
Expected: build FAIL (`SearchLanAsync`, `Partial`, the interface member).

- [ ] **Step 2: The service**

Rewrite `Programs/ConWatch/Discoveries/DiscoverySearchService.cs` so one runner handles both kinds of search:

```csharp
using System.Net.Sockets;
using ConInfrastructure.Discovery;
using ConPersistence.Discoveries;

namespace ConWatch.Discoveries;

/// <param name="Busy">Another search holds the gate.</param>
/// <param name="TimedOut">The search ran past the cap and had nothing to keep (router/edge searches).</param>
/// <param name="Partial">The scan was cut short by the cap; what it found so far was kept (LAN scan).</param>
public sealed record SearchOutcome(bool Busy, bool TimedOut, int Found, int Created, int Updated,
    string? Warning, IReadOnlyList<string> Errors, bool Partial = false)
{
    public static SearchOutcome IsBusy { get; } = new(true, false, 0, 0, 0, null, []);
}

/// <summary>
/// Runs the node's own searches one at a time with an overall time cap, and files what they
/// find in the inbox with Origin "server". The gate and the cap keep a stuck search from
/// wedging the page.
/// </summary>
public sealed class DiscoverySearchService(IDiscoveryScanner scanner, DiscoveryStore store,
    ILogger<DiscoverySearchService> log, TimeSpan? cap = null)
{
    public const string ServerOrigin = "server";
    public static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _cap = cap ?? DefaultCap;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<SearchOutcome> SearchRouterAsync(CancellationToken ct) =>
        RunAsync(async t => (await scanner.FindRouterAsync(t), true), ct);

    /// <param name="host">The public address to trace toward; null or blank uses <see cref="DiscoveryScanner.DefaultEdgeHost"/>.</param>
    public Task<SearchOutcome> SearchEdgeAsync(string? host, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(host) ? DiscoveryScanner.DefaultEdgeHost : host.Trim();
        return RunAsync(async t => (await scanner.FindNetworkEdgeAsync(target, t), true), ct);
    }

    /// <summary>The LAN scan keeps its partial results when the cap cuts it short.</summary>
    public Task<SearchOutcome> SearchLanAsync(CancellationToken ct) =>
        RunAsync(async t =>
        {
            var scan = await scanner.ScanLanAsync(t);
            return (scan.Found, scan.Complete);
        }, ct);

    private async Task<SearchOutcome> RunAsync(
        Func<CancellationToken, Task<(IReadOnlyList<DiscoveryCandidate> Found, bool Complete)>> search,
        CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
            return SearchOutcome.IsBusy;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_cap);
            IReadOnlyList<DiscoveryCandidate> found;
            bool complete;
            try
            {
                (found, complete) = await search(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("Discovery search exceeded its {Cap} cap", _cap);
                return new SearchOutcome(false, true, 0, 0, 0, Warning(), []);
            }
            catch (Exception ex) when (ex is ArgumentException or SocketException)
            {
                return new SearchOutcome(false, false, 0, 0, 0, null, [$"Cannot search: {ex.Message}"]);
            }
            // The client left: a scan that swallowed the cancellation and returned a partial list is not "done".
            ct.ThrowIfCancellationRequested();

            if (found.Count == 0)
                return new SearchOutcome(false, !complete, 0, 0, 0, Warning(), [], Partial: false);
            var stored = await store.UpsertAsync(found.Select(c => new DiscoveryInput(
                c.Address, c.Hostname, c.Kind, c.Method, ServerOrigin, c.Evidence)).ToList());
            return stored.Success
                ? new SearchOutcome(false, false, found.Count, stored.Created, stored.Updated, Warning(), [],
                    Partial: !complete)
                : new SearchOutcome(false, false, 0, 0, 0, null, stored.Errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Warning() => ContainerHint.IsLikelyContainer() ? ContainerHint.Warning : null;
}
```

`DiscoveryEndpoints.cs` — add the route beside the other searches and return `partial` in the success body:

```csharp
        d.MapPost("/search/lan", async (DiscoverySearchService search, CancellationToken ct) =>
            ToHttp(await search.SearchLanAsync(ct)));
```

and in `ToHttp(SearchOutcome)` change the success payload to
`new { found = outcome.Found, created = outcome.Created, updated = outcome.Updated, partial = outcome.Partial, warning = outcome.Warning }`.

`ConWatchServices.cs` — build the scanner with the system pieces (replace the `IDiscoveryScanner` registration from plan 2):

```csharp
        services.AddSingleton(sp => new ConInfrastructure.Discovery.LanScanner(
            new ConInfrastructure.Discovery.SystemLocalNetworks(),
            new ConInfrastructure.Discovery.SystemNeighborTable(),
            ConInfrastructure.Discovery.OuiTable.Default,
            ConInfrastructure.Discovery.LanScanner.IcmpPing,
            ConInfrastructure.Discovery.LanScanner.ReverseDns));
        services.AddSingleton<ConInfrastructure.Discovery.IDiscoveryScanner, ConInfrastructure.Discovery.DiscoveryScanner>();
```

- [ ] **Step 3: The page button**

In `Discoveries.cshtml` add beside the other two search buttons `<button type="button" id="find-lan" class="outline">Scan local network</button>`, extend the status handling so a partial result reads `Found N (X new) — stopped at the time limit; this is what it found so far.` (use `result.body.partial`), and wire it like the others:

```js
        document.getElementById("find-lan").addEventListener("click",
            (e) => search(e.currentTarget, "/api/v1/discoveries/search/lan", "Scanning your local network",
                "Nothing found: no device answered on this network."));
```

(`search` already takes the "nothing found" message as its fourth argument from plan 2's M2 change; the container warning still appends — and it matters most here: inside a bridged container the scan sees only the container's own subnet.)

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS, including `ApiSurfaceLockTests` unchanged (the new route is locked).

```bash
git add Programs Tests
git commit -m "feat(discoveries): server-side LAN scan with partial results; Scan local network button"
```

---

### Task 6: Adopting a network edge prefills its parent

**Files:**
- Modify: `Programs/ConWatch/Pages/Config/Discoveries.cshtml.cs`
- Test: modify `Tests/ConWatch.Tests/DiscoveriesPageTests.cs`

**Interfaces:**
- Consumes: the page model's existing `_targetNames`/address→target map (plan 2's I2 fix), `DiscoveryStore.ListAsync(null)`.
- Produces: `AdoptUrl` for a `NetworkEdge` adds `parent=<name>` when a router target is known: the live target of an **Adopted** Gateway discovery, else the target whose address equals a Gateway discovery's address (most recently seen first). No known router → no `parent` (unchanged).

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ConWatch.Tests/DiscoveriesPageTests.cs`:

```csharp
    [Fact]
    public async Task A_network_edge_adopt_link_names_the_router_target_as_its_parent()
    {
        await ConfigApiTests.SeedHomeAsync(_client);                       // Router at 192.168.1.1
        await PostAsync(Item("192.168.1.1", "Gateway", "DefaultRoute"), Item("203.0.113.5", "NetworkEdge", "Traceroute"));

        var html = System.Net.WebUtility.HtmlDecode(await _client.GetStringAsync("/Config/Discoveries"));

        Assert.Contains("address=203.0.113.5&name=ISP%20edge&profile=Path&scope=Verdict&parent=Router&discoveryId=", html);
    }

    [Fact]
    public async Task A_network_edge_adopt_link_follows_an_adopted_gateway_to_its_target_name()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await PostAsync(Item("192.0.2.1", "Gateway", "DefaultRoute"), Item("203.0.113.5", "NetworkEdge", "Traceroute"));
        var gatewayId = await IdOfAsync("192.0.2.1");
        (await _client.PostAsJsonAsync($"/api/v1/targets?discoveryId={gatewayId}",
            new { name = "Home Gateway", address = "192.0.2.1", profile = "Fast" })).EnsureSuccessStatusCode();

        var html = System.Net.WebUtility.HtmlDecode(await _client.GetStringAsync("/Config/Discoveries"));

        Assert.Contains("parent=Home%20Gateway&discoveryId=", html);
    }

    [Fact]
    public async Task Without_a_known_router_a_network_edge_adopt_link_has_no_parent()
    {
        await PostAsync(Item("203.0.113.5", "NetworkEdge", "Traceroute"));
        var html = System.Net.WebUtility.HtmlDecode(await _client.GetStringAsync("/Config/Discoveries"));
        Assert.Contains("address=203.0.113.5", html);
        Assert.DoesNotContain("parent=", html);
    }
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~DiscoveriesPageTests"`
Expected: the first two FAIL (no `parent=` in the link); the third passes.

- [ ] **Step 2: Implement**

In `Discoveries.cshtml.cs`, in `OnGetAsync` after the target maps are built, compute and keep the router name:

```csharp
        var all = await store.ListAsync(null);   // every status: the gateway may already be adopted or dismissed
        var gateways = all.Where(d => d.Kind == DiscoveryKind.Gateway).OrderByDescending(d => d.LastSeenUtc).ToList();
        _routerName =
            gateways.Where(d => d.Status == DiscoveryStatus.Adopted && d.AdoptedTargetName is not null)
                .Select(d => d.AdoptedTargetName).FirstOrDefault()
            ?? gateways.Select(d => MonitoredAs(d)).FirstOrDefault(n => n is not null);
```

with a field `private string? _routerName;`, and in `AdoptUrl` build the query so the NetworkEdge case inserts the parent **between `scope` and `discoveryId`**:

```csharp
        var query = new QueryString()
            .Add("address", d.Address).Add("name", name).Add("profile", profile).Add("scope", scope);
        if (d.Kind == DiscoveryKind.NetworkEdge && _routerName is { Length: > 0 })
            query = query.Add("parent", _routerName);
        query = query.Add("discoveryId", d.Id.ToString());
        return "/Config/Targets" + query;
```

(`MonitoredAs` is the plan-2 helper that returns the name of the target at a discovery's address, or null.)

- [ ] **Step 3: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~DiscoveriesPageTests"` then the full project.
Expected: all PASS (the existing exact-query-string tests still hold: Gateway and LocalDevice links carry no `parent`).

```bash
git add Programs Tests
git commit -m "feat(discoveries): adopting a network edge prefills the router as its parent"
```

---

### Task 7: MCP `list_discoveries` follows the admin gate (parity with the web UI)

**Files:**
- Modify: `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`, `Programs/ConWatch/Startup/ConWatchServices.cs`, `SECURITY.md`
- Test: modify `Tests/ConWatch.Tests/McpEndpointTests.cs`

**Interfaces:**
- Consumes: `AdminGate.IsLocked(ClaimsPrincipal)` (true when an admin password is set and the caller lacks the admin cookie — the same predicate `RequireAdminFilter` and the page filter use), `DiscoveryStore`.
- Produces: `list_discoveries` answers normally when no admin password is set or the caller carries the admin cookie; otherwise it throws an `McpException` with a readable message. `get_current_status`, `get_connection_report`, `get_transitions` and `get_glitches` are unchanged (they stay open, as the watching surface is). The tool still lists in `tools/list`.
- Decision (user, 2026-10-06): *"If a user can do it from the web UI without entering an admin password, they should be able to do it from MCP. And if not, then not."*

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ConWatch.Tests/McpEndpointTests.cs`. First split the JSON-RPC helper so any client can be used: change `RpcAsync(string method, object? @params = null)` into a thin wrapper `=> RpcWithAsync(_client, method, @params)` and move its body into

```csharp
    private async Task<JsonElement> RpcWithAsync(HttpClient client, string method, object? @params = null)
```

(the same request construction, using `client` instead of `_client`). Then:

```csharp
    private async Task SeedOneDiscoveryAsync() =>
        (await _client.PostAsJsonAsync("/api/v1/discoveries", new
        {
            items = new[] { new { address = "192.0.2.50", kind = "LocalDevice", method = "ArpTable", origin = "tray:PC" } },
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task list_discoveries_is_open_while_no_admin_password_is_set()
    {
        await SeedOneDiscoveryAsync();
        await InitializeAsync();

        var result = await RpcAsync("tools/call", new { name = "list_discoveries", arguments = new { } });

        Assert.False(result.TryGetProperty("isError", out var e) && e.GetBoolean());
        Assert.Contains("192.0.2.50", ToolText(result));
    }

    [Fact]
    public async Task list_discoveries_needs_admin_mode_once_a_password_is_set_exactly_like_the_page()
    {
        await SeedOneDiscoveryAsync();
        // set-password leaves _client signed in as admin; a second client has no cookie.
        (await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var anonymous = _factory.CreateClient();

        var denied = await RpcWithAsync(anonymous, "tools/call", new { name = "list_discoveries", arguments = new { } });
        var allowed = await RpcWithAsync(_client, "tools/call", new { name = "list_discoveries", arguments = new { } });

        Assert.True(denied.GetProperty("isError").GetBoolean());
        Assert.Contains("admin", ToolText(denied), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.50", ToolText(denied));        // nothing leaks through the error
        Assert.Contains("192.0.2.50", ToolText(allowed));

        // The watching tools stay open to the same anonymous client.
        var status = await RpcWithAsync(anonymous, "tools/call", new { name = "get_current_status", arguments = new { } });
        Assert.False(status.TryGetProperty("isError", out var e) && e.GetBoolean());
    }

    [Fact]
    public async Task The_tool_is_still_listed_for_everyone()
    {
        (await _client.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var anonymous = _factory.CreateClient();
        await RpcWithAsync(anonymous, "initialize", new
        {
            protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "t", version = "1" },
        });
        var tools = (await RpcWithAsync(anonymous, "tools/list")).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("list_discoveries", tools);
    }
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~McpEndpointTests"`
Expected: the "needs admin mode" test FAILS (the anonymous client reads the inbox today).

- [ ] **Step 2: Implement**

`ConWatchServices.cs` — add `services.AddHttpContextAccessor();` near the other framework registrations.

`ConWatchMcpTools.cs` — add `using System.Security.Claims;`, `using ConWatch.Auth;`, and extend the primary constructor with `AdminGate gate, IHttpContextAccessor http`:

```csharp
public class ConWatchMcpTools(WatcherHostService host,
    IDbContextFactory<ConWatchDbContext> dbFactory, IspReportBuilder reportBuilder,
    DiscoveryStore discoveries, AdminGate gate, IHttpContextAccessor http)
```

and guard the tool (keep the existing description, adding the sentence about admin mode):

```csharp
        // Parity with the web UI: whatever the Discoveries page needs admin mode for, this does too.
        if (gate.IsLocked(http.HttpContext?.User ?? new ClaimsPrincipal()))
            throw new McpException("list_discoveries needs admin mode: an admin password is set on this server " +
                "and the Discoveries inbox is admin-only, like its web page. Sign in as admin in the web UI, " +
                "or ask the owner to remove the password.");
```

(place it before the status parsing). Extend the tool's `[Description]` with: "When the server has an admin password set this tool requires admin mode, like the Discoveries page; without a password it is open." Update the class-level doc comment from plan 2 so it no longer says the tool is open regardless: "…plus `list_discoveries`, which reads the Discoveries inbox under the same access rule as its web page (open without an admin password, admin-only with one)."

If `http.HttpContext` turns out to be null for stateless MCP requests, report it — do not guess a fallback that opens the tool.

`SECURITY.md` — in the first bullet of "Security posture", replace the sentence plan 2 added about `list_discoveries` with: "The Discoveries inbox (every router, network-edge and local-device candidate a search or a tray scout found, with hostnames and the scout's machine name) follows the admin gate: its page, its REST API and the MCP `list_discoveries` tool are open only while no admin password is set."

- [ ] **Step 3: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS (including the plan-2 MCP tests, which run without a password).

```bash
git add Programs Tests SECURITY.md
git commit -m "feat(mcp): list_discoveries follows the admin gate, matching the Discoveries page"
```

---

### Task 8: The scout client (and `TransportPolicy` moves to the shared library)

**Files:**
- Move: `Programs/ConWatch/Clone/TransportPolicy.cs` → `Libraries/ConInfrastructure/Net/TransportPolicy.cs` (namespace `ConInfrastructure.Net`)
- Create: `Libraries/ConInfrastructure/Discovery/ScoutClient.cs`
- Modify: `Programs/ConWatch/Clone/CloneService.cs` (using), `Tests/ConWatch.Tests/TransportPolicyTests.cs` (using)
- Test: create `Tests/ConWatch.Tests/ScoutClientTests.cs`

**Interfaces:**
- Produces:
  - `ConInfrastructure.Net.TransportPolicy` — the same `Evaluate(Uri source, bool sensitive, bool allowInsecure)` and `Verdict` as today, just relocated (the tray and the server both enforce it).
  - `ScoutItem(string Address, string? Hostname, DiscoveryKind Kind, DiscoveryMethod Method, string Origin, string? Evidence)`; `ScoutFailure { None, Unreachable, PasswordRequired, WrongPassword, InsecureTransport, Rejected, ServerError }`; `ScoutResult(bool Success, ScoutFailure Failure, string Message, int Created = 0, int Updated = 0)`.
  - `ScoutClient(HttpClient http, Uri server)` — the `HttpClient` must carry cookies (the tray gives it a cookie-enabled handler). `Task<ScoutResult> SendAsync(IReadOnlyList<ScoutItem> items, string? adminPassword, bool allowInsecure, CancellationToken ct)`:
    1. `POST {server}/api/v1/discoveries` with `{ "items": [...] }`. `200` → success with the server's created/updated counts.
    2. `401` → if `adminPassword` is null → `PasswordRequired`; else evaluate `TransportPolicy` with `sensitive: true` **before the password is sent** (`RequiresOverride` and not `allowInsecure` → `InsecureTransport`); then `POST /api/v1/auth/login { password }` (`401` → `WrongPassword`, any other failure → `ServerError`), then retry the post once.
    3. `400` → `Rejected` with the server's messages joined; `5xx`/other → `ServerError`; `HttpRequestException` or a request timeout → `Unreachable`.
  - Messages are plain sentences safe to show in a balloon; **no message ever contains the password**.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConWatch.Tests/ScoutClientTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Discovery;

namespace ConWatch.Tests;

public class ScoutClientTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static readonly ScoutItem[] OneDevice =
    [
        new("192.168.1.20", "nas.example.com", DiscoveryKind.LocalDevice, DiscoveryMethod.ArpTable,
            "tray:TESTPC", "MAC aa:bb:cc:00:11:33"),
    ];

    private HttpClient Client() => _factory.CreateClient();          // cookie-enabled, in-memory server

    private static Uri Local => new("http://localhost/");

    private async Task<JsonElement> ListAsync(HttpClient admin) =>
        JsonSerializer.Deserialize<JsonElement>(await admin.GetStringAsync("/api/v1/discoveries?status=all"));

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw ex;
    }

    [Fact]
    public async Task An_open_server_accepts_the_post_without_any_password()
    {
        var client = Client();

        var result = await new ScoutClient(client, Local).SendAsync(OneDevice, null, false, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal((1, 0), (result.Created, result.Updated));
        var row = Assert.Single((await ListAsync(client)).EnumerateArray());
        Assert.Equal("tray:TESTPC", row.GetProperty("origin").GetString());
        Assert.Equal("nas.example.com", row.GetProperty("hostname").GetString());
    }

    [Fact]
    public async Task A_locked_server_without_a_password_says_a_password_is_required()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();

        var result = await new ScoutClient(Client(), Local).SendAsync(OneDevice, null, false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScoutFailure.PasswordRequired, result.Failure);
        Assert.Empty((await ListAsync(admin)).EnumerateArray());
    }

    [Fact]
    public async Task A_wrong_password_is_reported_and_nothing_is_stored()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();

        var result = await new ScoutClient(Client(), Local).SendAsync(OneDevice, "wrong", false, CancellationToken.None);

        Assert.Equal(ScoutFailure.WrongPassword, result.Failure);
        Assert.DoesNotContain("wrong", result.Message);
        Assert.Empty((await ListAsync(admin)).EnumerateArray());
    }

    [Fact]
    public async Task The_right_password_logs_in_posts_and_keeps_the_session_for_the_next_send()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var scout = new ScoutClient(Client(), Local);

        var first = await scout.SendAsync(OneDevice, "hunter2", false, CancellationToken.None);
        var second = await scout.SendAsync(OneDevice, null, false, CancellationToken.None);   // cookie, no password

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        Assert.Equal((0, 1), (second.Created, second.Updated));
    }

    [Fact]
    public async Task The_password_is_never_sent_over_plain_http_to_another_host_without_an_explicit_yes()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var remote = new Uri("http://scout-test.example.com/");   // plain http, not loopback

        var blocked = await new ScoutClient(Client(), remote).SendAsync(OneDevice, "hunter2", false, CancellationToken.None);
        var allowed = await new ScoutClient(Client(), remote).SendAsync(OneDevice, "hunter2", true, CancellationToken.None);

        Assert.Equal(ScoutFailure.InsecureTransport, blocked.Failure);
        Assert.True(allowed.Success, allowed.Message);
    }

    [Fact]
    public async Task An_https_server_needs_no_override()
    {
        var admin = Client();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();

        var result = await new ScoutClient(Client(), new Uri("https://scout-test.example.com/"))
            .SendAsync(OneDevice, "hunter2", false, CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task A_batch_the_server_rejects_comes_back_as_Rejected_with_the_servers_words()
    {
        var bad = new[] { new ScoutItem("router.example.com", null, DiscoveryKind.Gateway, DiscoveryMethod.DefaultRoute, "tray:X", null) };

        var result = await new ScoutClient(Client(), Local).SendAsync(bad, null, false, CancellationToken.None);

        Assert.Equal(ScoutFailure.Rejected, result.Failure);
        Assert.Contains("not a usable IP address", result.Message);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]     // an HttpClient timeout looks like this
    public async Task An_unreachable_or_timing_out_server_is_Unreachable_and_names_the_address(Type exceptionType)
    {
        var http = new HttpClient(new ThrowingHandler((Exception)Activator.CreateInstance(exceptionType, "nope")!));

        var result = await new ScoutClient(http, new Uri("http://nas.example.com:8080/"))
            .SendAsync(OneDevice, null, false, CancellationToken.None);

        Assert.Equal(ScoutFailure.Unreachable, result.Failure);
        Assert.Contains("nas.example.com", result.Message);
    }

    [Fact]
    public async Task A_cancelled_send_is_cancellation_not_Unreachable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ScoutClient(Client(), Local).SendAsync(OneDevice, null, false, cts.Token));
    }
}
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~ScoutClientTests"`
Expected: build FAIL — `ScoutClient`, `ScoutItem`, … do not exist.

- [ ] **Step 2: Move `TransportPolicy`**

`git mv Programs/ConWatch/Clone/TransportPolicy.cs Libraries/ConInfrastructure/Net/TransportPolicy.cs`, change its namespace to `ConInfrastructure.Net`, and add `using ConInfrastructure.Net;` to `Programs/ConWatch/Clone/CloneService.cs` and `Tests/ConWatch.Tests/TransportPolicyTests.cs` (replace the `ConWatch.Clone` using there if present). Run `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TransportPolicyTests|FullyQualifiedName~CloneTests"` — Expected: PASS unchanged (a pure move).

- [ ] **Step 3: The client**

Create `Libraries/ConInfrastructure/Discovery/ScoutClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Net;

namespace ConInfrastructure.Discovery;

public sealed record ScoutItem(string Address, string? Hostname, DiscoveryKind Kind, DiscoveryMethod Method,
    string Origin, string? Evidence);

public enum ScoutFailure
{
    None,
    Unreachable,
    PasswordRequired,
    WrongPassword,
    InsecureTransport,
    Rejected,
    ServerError,
}

public sealed record ScoutResult(bool Success, ScoutFailure Failure, string Message, int Created = 0, int Updated = 0)
{
    public static ScoutResult Fail(ScoutFailure failure, string message) => new(false, failure, message);
}

/// <summary>
/// The tray scout's HTTP side: post discoveries to a node, signing in with the admin password only when
/// the node answers 401, and never sending that password over plain HTTP to another machine without an
/// explicit yes (<see cref="TransportPolicy"/>). The caller supplies an <see cref="HttpClient"/> with
/// cookies enabled, so one sign-in covers later sends. Messages never contain the password.
/// </summary>
public sealed class ScoutClient(HttpClient http, Uri server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Uri Url(string path) => new(server.AbsolutePath.EndsWith('/') ? server : new Uri(server + "/"), path);

    public async Task<ScoutResult> SendAsync(IReadOnlyList<ScoutItem> items, string? adminPassword,
        bool allowInsecure, CancellationToken ct)
    {
        try
        {
            var response = await PostItemsAsync(items, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (adminPassword is null)
                    return ScoutResult.Fail(ScoutFailure.PasswordRequired,
                        "This server needs its admin password before it will accept discoveries.");
                // The password is about to cross the wire: decide now, before it does.
                if (TransportPolicy.Evaluate(server, sensitive: true, allowInsecure) == TransportPolicy.Verdict.RequiresOverride)
                    return ScoutResult.Fail(ScoutFailure.InsecureTransport,
                        $"Signing in would send your admin password to {server.Host} unencrypted over plain HTTP.");

                var login = await http.PostAsJsonAsync(Url("api/v1/auth/login"), new { password = adminPassword }, Json, ct);
                if (login.StatusCode == HttpStatusCode.Unauthorized)
                    return ScoutResult.Fail(ScoutFailure.WrongPassword, "The admin password was not accepted.");
                if (!login.IsSuccessStatusCode)
                    return ScoutResult.Fail(ScoutFailure.ServerError,
                        $"The server refused the sign-in ({(int)login.StatusCode}).");
                response = await PostItemsAsync(items, ct);
            }
            return await InterpretAsync(response, ct);
        }
        catch (HttpRequestException ex)
        {
            return Unreachable(ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Unreachable(ex);       // the HttpClient's own timeout
        }
    }

    private Task<HttpResponseMessage> PostItemsAsync(IReadOnlyList<ScoutItem> items, CancellationToken ct) =>
        http.PostAsJsonAsync(Url("api/v1/discoveries"), new { items }, Json, ct);

    private ScoutResult Unreachable(Exception ex) =>
        ScoutResult.Fail(ScoutFailure.Unreachable,
            $"Could not reach the server at {server.Authority}: {ex.GetBaseException().Message}");

    private static async Task<ScoutResult> InterpretAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                return new ScoutResult(true, ScoutFailure.None, "Sent.",
                    json.GetProperty("created").GetInt32(), json.GetProperty("updated").GetInt32());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return ScoutResult.Fail(ScoutFailure.ServerError, "The server answered, but not like a ConnectionWatcher node.");
            }
        }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var errors = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("errors")
                    .EnumerateArray().Select(e => e.GetString()).Where(e => e is not null);
                return ScoutResult.Fail(ScoutFailure.Rejected, "The server rejected the results: " + string.Join(" ", errors));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return ScoutResult.Fail(ScoutFailure.Rejected, "The server rejected the results.");
            }
        }
        return ScoutResult.Fail(ScoutFailure.ServerError, $"The server answered {(int)response.StatusCode}.");
    }
}
```

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS.

```bash
git add Libraries Programs Tests
git commit -m "feat(scout): ScoutClient posts discoveries, signs in on 401 and guards the password; TransportPolicy moves to ConInfrastructure"
```

---

### Task 9: The tray scouts

**Files:**
- Modify: `Programs/ConWatchTray/ConWatchTray.csproj`, `Programs/ConWatchTray/Program.cs`, `Programs/ConWatchTray/SettingsForm.cs`
- Create: `Programs/ConWatchTray/ScoutPasswordStore.cs`, `Programs/ConWatchTray/ScoutRunner.cs`, `Programs/ConWatchTray/PasswordPrompt.cs`
- Test: none (the tray has no test project; its logic lives in the tested `ScoutClient` and scanners). Verify with `dotnet build Programs/ConWatchTray -c Debug` on Windows and the manual checklist in Task 11.

**Interfaces:**
- Consumes: `ScoutClient`/`ScoutItem`/`ScoutFailure` (Task 8), `DiscoveryScanner`/`LanScanner` (Tasks 4), `GatewayDiscovery`, `TracerouteRunner`, `IcmpTtlProbe`, `NullAsnLookup`, `TraySettings.EffectiveUrl`, `ConfigPaths.Default().UserFile` (its directory holds the tray's per-user files).
- Produces: three new context-menu items — **Find my router**, **Find the network edge**, **Scan this network** — that run the scanner on this PC (30 s cap, partial LAN results kept), post the results (`Origin` = `tray:<machine name>`) to the configured server, and report plainly in a balloon; a stored admin password protected with DPAPI (current user), bound to the server URL it was entered for; **Forget** in the tray settings.

- [ ] **Step 1: Project references**

`ConWatchTray.csproj` — add to the `<ItemGroup>` with the ConConfig reference:

```xml
    <ProjectReference Include="..\..\Libraries\ConInfrastructure\ConInfrastructure.csproj" />
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.*" />
```

(use the version scheme the repo's other package references use — check `Directory.Packages.props` or a sibling csproj and match it; the tray is the only project that needs DPAPI).

- [ ] **Step 2: The password store**

Create `Programs/ConWatchTray/ScoutPasswordStore.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConConfig;

namespace ConWatchTray;

/// <summary>
/// The admin password the scout uses, protected with DPAPI for the current Windows user and stored
/// next to tray.json. It is bound to the server URL it was entered for: a different URL never reuses it.
/// A file that cannot be read or decrypted is treated as "no saved password".
/// </summary>
internal sealed class ScoutPasswordStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ConnectionWatcher.Scout.v1");

    private sealed record Stored(string Server, string Protected);

    private readonly string _path;

    public ScoutPasswordStore(ConfigPaths? paths = null)
    {
        var dir = Path.GetDirectoryName((paths ?? ConfigPaths.Default()).UserFile)!;
        _path = Path.Combine(dir, "scout.json");
    }

    public string? Load(string serverUrl)
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (stored is null || !string.Equals(Normalize(stored.Server), Normalize(serverUrl), StringComparison.OrdinalIgnoreCase))
                return null;
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.Protected), Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(string serverUrl, string password)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy,
                DataProtectionScope.CurrentUser);
            var json = JsonSerializer.Serialize(new Stored(Normalize(serverUrl), Convert.ToBase64String(protectedBytes)));
            File.WriteAllText(_path, json);
        }
        catch (Exception)
        {
            // A password that cannot be saved is simply asked for again next time.
        }
    }

    public bool HasSaved => File.Exists(_path);

    public void Forget()
    {
        try { File.Delete(_path); } catch (Exception) { }
    }

    private static string Normalize(string url) => url.TrimEnd('/');
}
```

- [ ] **Step 3: The runner**

Create `Programs/ConWatchTray/ScoutRunner.cs`:

```csharp
using ConCore.Discovery;
using ConInfrastructure.Asn;
using ConInfrastructure.Discovery;
using ConInfrastructure.Traceroute;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatchTray;

internal enum ScoutKind { Router, NetworkEdge, Lan }

/// <summary>Runs the shared scanners on this PC (30 s cap) and turns what they find into <see cref="ScoutItem"/>s.</summary>
internal sealed class ScoutRunner
{
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    private readonly DiscoveryScanner _scanner = new(
        new GatewayDiscovery(),
        new TracerouteRunner(new IcmpTtlProbe(), NullAsnLookup.Instance, NullLogger<TracerouteRunner>.Instance),
        new LanScanner(new SystemLocalNetworks(), new SystemNeighborTable(), OuiTable.Default,
            LanScanner.IcmpPing, LanScanner.ReverseDns));

    public string Origin => $"tray:{Environment.MachineName}";

    /// <returns>The items found and whether a LAN scan was cut short by the cap.</returns>
    public async Task<(IReadOnlyList<ScoutItem> Items, bool Partial)> RunAsync(ScoutKind kind)
    {
        using var cts = new CancellationTokenSource(Cap);
        IReadOnlyList<DiscoveryCandidate> found;
        var partial = false;
        try
        {
            switch (kind)
            {
                case ScoutKind.Router:
                    found = await _scanner.FindRouterAsync(cts.Token);
                    break;
                case ScoutKind.NetworkEdge:
                    found = await _scanner.FindNetworkEdgeAsync(DiscoveryScanner.DefaultEdgeHost, cts.Token);
                    break;
                default:
                    var lan = await _scanner.ScanLanAsync(cts.Token);
                    found = lan.Found;
                    partial = !lan.Complete;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            found = [];                      // router/edge have nothing to keep when cut short
        }
        var items = found.Select(c => new ScoutItem(c.Address, c.Hostname, c.Kind, c.Method, Origin, c.Evidence)).ToList();
        return (items, partial);
    }
}
```

- [ ] **Step 4: The password prompt**

Create `Programs/ConWatchTray/PasswordPrompt.cs`:

```csharp
namespace ConWatchTray;

/// <summary>A small modal asking for the server's admin password, with a "remember on this PC" box.</summary>
internal sealed class PasswordPrompt : Form
{
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    private readonly CheckBox _remember = new() { Text = "Remember on this PC (encrypted for your Windows account)", AutoSize = true, Checked = true, Dock = DockStyle.Top };

    public string Password => _password.Text;
    public bool Remember => _remember.Checked;

    public PasswordPrompt(string serverUrl, string? reason)
    {
        Text = "ConnectionWatcher — admin password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(420, 150);
        Padding = new Padding(12);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 80 };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(buttons);
        Controls.Add(_remember);
        Controls.Add(_password);
        Controls.Add(new Label
        {
            Text = (reason is { Length: > 0 } ? reason + "\n" : "") + $"Enter the admin password for {serverUrl}:",
            AutoSize = false, Dock = DockStyle.Top, Height = 48,
        });
    }
}
```

- [ ] **Step 5: Wire the menu and the flow**

`Program.cs` — in `TrayContext`, add fields:

```csharp
    private readonly ScoutRunner _scout = new();
    private readonly ScoutPasswordStore _scoutPasswords = new();
    // One cookie-enabled client for the scout (its own, longer timeout): a sign-in is reused by later sends.
    private readonly HttpClient _scoutHttp = new(ConConfig.LocalServerHttp.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };
    private bool _scoutAllowInsecure;          // the user's explicit yes, for this tray session only
    private bool _scouting;
    private string? _balloonUrl;
```

Menu — after the "Server settings" item add a separator and:

```csharp
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Find my router", null, async (_, _) => await ScoutAsync(ScoutKind.Router));
        menu.Items.Add("Find the network edge", null, async (_, _) => await ScoutAsync(ScoutKind.NetworkEdge));
        menu.Items.Add("Scan this network", null, async (_, _) => await ScoutAsync(ScoutKind.Lan));
```

and in the constructor after `_icon` is created: `_icon.BalloonTipClicked += (_, _) => { if (_balloonUrl is { } url) Open(url); };`.

The flow (add to the class):

```csharp
    private async Task ScoutAsync(ScoutKind kind)
    {
        if (_scouting)
        {
            Balloon("Scouting", "A search is already running — wait for it to finish.", ToolTipIcon.Info);
            return;
        }
        _scouting = true;
        try
        {
            var label = kind switch { ScoutKind.Router => "your router", ScoutKind.NetworkEdge => "the network edge", _ => "this network" };
            Balloon("Scouting", $"Looking for {label}…", ToolTipIcon.Info);

            var (items, partial) = await _scout.RunAsync(kind);
            if (items.Count == 0)
            {
                Balloon("Nothing found", kind == ScoutKind.NetworkEdge
                    ? "The hops past your router did not answer. Try again in a moment."
                    : "Nothing was found from this PC.", ToolTipIcon.Info);
                return;
            }

            var server = _settings.EffectiveUrl;
            var client = new ScoutClient(_scoutHttp, new Uri(server.TrimEnd('/') + "/"));
            var password = _scoutPasswords.Load(server);
            string? reason = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var result = await client.SendAsync(items, password, _scoutAllowInsecure, CancellationToken.None);
                switch (result.Failure)
                {
                    case ScoutFailure.None:
                        Balloon("Sent to ConnectionWatcher",
                            $"{items.Count} found, {result.Created} new{(partial ? " (the scan was cut short)" : "")}. Click to review.",
                            ToolTipIcon.Info, "/Config/Discoveries");
                        return;
                    case ScoutFailure.PasswordRequired:
                    case ScoutFailure.WrongPassword:
                        reason = result.Failure == ScoutFailure.WrongPassword ? "That password was not accepted." : null;
                        password = PromptForPassword(server, reason);
                        if (password is null)
                            return;                       // cancelled
                        break;
                    case ScoutFailure.InsecureTransport:
                        var yes = MessageBox.Show(
                            $"{result.Message}\n\nSend it anyway? (Anyone on your network could read it. Prefer an https:// server address.)",
                            "ConnectionWatcher — unencrypted password",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                        if (!yes)
                            return;
                        _scoutAllowInsecure = true;
                        break;
                    default:
                        Balloon("Could not send", result.Message, ToolTipIcon.Warning);
                        return;
                }
            }
            Balloon("Could not send", "The admin password was not accepted.", ToolTipIcon.Warning);
        }
        finally
        {
            _scouting = false;
        }
    }

    /// <summary>Asks for the password; a typed one is remembered (DPAPI) only when the box is ticked and the server later accepts it.</summary>
    private string? PromptForPassword(string server, string? reason)
    {
        using var prompt = new PasswordPrompt(server, reason);
        if (prompt.ShowDialog() != DialogResult.OK || prompt.Password.Length == 0)
            return null;
        if (prompt.Remember)
            _scoutPasswords.Save(server, prompt.Password);
        return prompt.Password;
    }

    private void Balloon(string title, string text, ToolTipIcon icon, string? openPath = null)
    {
        _balloonUrl = openPath;
        _icon.ShowBalloonTip(6000, title, Truncate(text, 250), icon);
    }
```

(A wrong password that was *saved* by "remember" gets replaced the next time the user types a good one — `Save` overwrites. A password is held only in local variables and the DPAPI blob; it is never logged, never put in a balloon.)

Also dispose `_scoutHttp` in `ExitThreadCore`.

`SettingsForm.cs` — add a **Forget saved admin password** button next to the existing controls, enabled only when `ScoutPasswordStore.HasSaved`, which calls `Forget()` and disables itself (follow the form's existing layout code for placement).

- [ ] **Step 6: Build and commit**

Run: `dotnet build Programs/ConWatchTray -c Debug` (Windows). Expected: 0 errors, no new warnings. The tray has no automated tests; do not claim it verified beyond the build — Task 11 lists the manual checks.

```bash
git add Programs
git commit -m "feat(tray): Find my router / network edge / Scan this network — scout results to the server with a DPAPI-stored admin password"
```

---

### Task 10: Host-network docs for NAS containers

**Files:**
- Modify: `deploy/docker/compose.yaml`
- Create: `deploy/docker/README.md`

**Interfaces:** documentation only (no code). Do not edit the top-level `README.md`.

- [ ] **Step 1: The compose file**

In `deploy/docker/compose.yaml`, extend the header comment (after the existing NOTE) with:

```yaml
# NETWORK MODE: the default (bridge) works for monitoring, but from inside a bridge network the
# container cannot see your LAN: a traceroute's first hop is the Docker bridge, "Find my router"
# returns the bridge, and "Scan local network" scans the bridge. On a Linux host (Synology, QNAP,
# Unraid, a Linux server) host networking fixes all three — see the commented block below and
# deploy/docker/README.md. Docker Desktop (Windows/macOS) does not support host networking.
```

and at the end of the `conwatch:` service add:

```yaml
    # --- Optional: host networking (Linux/NAS) -------------------------------------------------
    # Uncomment these three lines AND remove the `ports:` block above (ports are ignored in host
    # mode, and Compose rejects the combination on some versions). The app then listens on the
    # NAS's own network stack, so pick a free port: DSM uses 5000/5001 and 8080 is common.
    # network_mode: host
    # environment:
    #   ASPNETCORE_URLS: "http://0.0.0.0:18080"
```

- [ ] **Step 2: The README**

Create `deploy/docker/README.md`:

```markdown
# Running ConnectionWatcher in a container

```bash
cd deploy/docker && docker compose up -d
```

The container needs `NET_RAW` (already in `compose.yaml`) so ping and traceroute work.

## Bridge or host networking?

| | Bridge (default) | Host (`network_mode: host`) |
|---|---|---|
| Monitoring your targets | works | works |
| Traceroute's first hop | the Docker bridge, not your router | your real router |
| **Find my router** | returns the bridge | the real default gateway |
| **Scan local network** (Config → Discoveries) | scans the bridge subnet only | scans your real LAN |
| Port mapping | `ports:` (left side is yours to choose) | none — the app binds the host's port directly |
| Where it works | everywhere | **Linux hosts only** (Synology, QNAP, Unraid, Linux servers); Docker Desktop on Windows/macOS ignores it |

If you stay on bridge networking you lose nothing for monitoring. To discover your router and LAN devices
anyway, run the searches from a computer on your LAN instead: the Windows tray's **Find my router**,
**Find the network edge** and **Scan this network** menu items scan from the PC and post the results to
the container's Discoveries page (you will be asked for the admin password once if one is set).

## Switching to host networking

1. In `compose.yaml`, uncomment `network_mode: host` and the `ASPNETCORE_URLS` line, and **remove** the
   `ports:` block. Pick a free port in `ASPNETCORE_URLS` (DSM itself uses 5000/5001; many NAS apps use 8080).
2. `docker compose up -d`. The web page is now at `http://<nas-address>:<that port>/`.
3. Point the Windows tray (Tray settings) at the new URL.

**Synology Container Manager:** in the project/container network settings choose *Use the same network as
Docker host* instead of editing `ports:`.

## Notes

- With host networking the container sees every interface on the NAS. The LAN scan sweeps at most the /24
  around each private address, never public ranges.
- A container that still looks like a bridge (`DOTNET_RUNNING_IN_CONTAINER`) shows a warning on search results
  that the gateway may be the bridge; that warning is the cue to use host networking or the tray.
- Back up `./data` before pulling a new image: releases can include database migrations.
```

- [ ] **Step 3: Check and commit**

Run `docker compose -f deploy/docker/compose.yaml config` if Docker is available (expected: valid YAML, no warnings); otherwise validate the file parses as YAML (`pwsh -c "ConvertFrom-Yaml (Get-Content deploy/docker/compose.yaml -Raw)"` or a Python/PyYAML one-liner if present) and say which you used. Do not start containers.

```bash
git add deploy
git commit -m "docs(deploy): host-network option and bridge-vs-host guide for NAS containers"
```

---

### Task 11: Whole-branch verification, browser check and the tray checklist

**Files:** none (verification only; fix any defect in the owning task's files).

- [ ] **Step 1: Run every automated check**

```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
dotnet test Tests/ConNotify.Tests -c Debug
dotnet build Programs/ConWatchTray -c Debug
cd Programs/ConWatch/ClientApp && npx tsc --noEmit && npx vitest run && npm run build
```

Expected: all PASS (ConWatch known flake aside), tray builds. Baselines before this plan: ConEngine 234, ConPersistence 181, ConWatch 290, ConConfig 36, ConWatchCli 64, ConNotify 32, vitest 69.

- [ ] **Step 2: Browser check against a scratch instance**

Run THIS WORKTREE's own Release build on a free loopback port with a SCRATCH database (`dotnet run --project Programs/ConWatch -c Release -- --urls http://localhost:18099 --Persistence:DbPath <scratch>/conwatch.db`); do **not** use `preview_start conwatch-preview` (it launches the main checkout's binary). Never touch the user's real service, tray or data directory. Seed the usual `Fast` profile, `Router` (127.0.0.1) and two anchors. Then, with the built-in browser:

1. `/Config/Discoveries` → **Scan local network**. It runs a real scan of this machine (it can take up to ~30 s on a /24): the status line reports found/new counts; LocalDevice rows appear with method "ARP table" or "ping sweep", evidence containing a MAC and — where the registry knows it — a vendor name, or "private (randomized) address"; the router/default gateway and this machine's own address are **not** listed as devices. Record **counts only** — no addresses, MACs or hostnames in the report. If the scan reports it was cut short ("this is what it found so far"), note it.
2. **Adopt** one found device: the Targets dialog opens pre-filled (scope LAN device, profile Local, Parent disabled); save it; the Discoveries page shows it Adopted. **Dismiss** another; confirm a rescan does not resurrect it.
3. Scan twice quickly: the second request while the first runs shows the "another search is already running" error, then the first completes.
4. Network edge: post a `NetworkEdge` discovery and a gateway matching your `Router` target by API; confirm the edge row's **Adopt** link carries `parent=Router`.
5. MCP parity: with no admin password `tools/call list_discoveries` returns the inbox; after `POST /api/v1/auth/set-password`, an unauthenticated MCP call errors with an "admin" message and leaks no addresses, while the same call with the admin cookie works; `tools/list` still lists the tool; `get_current_status` stays open.
6. Scout end to end (server side): with an admin password set, drive the scout flow with `curl`/PowerShell exactly as `ScoutClient` does — `POST /api/v1/discoveries` unauthenticated → 401; `POST /api/v1/auth/login`; retry → 200 — confirming the contract the tray relies on.
7. `/Config/Targets` shows no regression on prefill (Adopt → dialog, cancel, reload — plan 2's one-shot behaviour).

Stop the app and delete the scratch data.

- [ ] **Step 3: The tray checklist (manual — the tray has no automated tests)**

Write into the report, for the user to run on their Windows PC against a real server, and say plainly that these were **not** run by the agent: (a) tray menu shows the three new items; (b) **Find my router** with no server password → balloon "Sent … Click to review", clicking opens `/Config/Discoveries`; (c) with an admin password set → a prompt; a wrong password → prompt again with "not accepted"; the right one → sent; **Remember** ticked → the next scout sends without a prompt; (d) a plain-`http://` non-loopback server → the unencrypted-password warning appears before the password is sent, **No** aborts without sending; (e) a stopped server → "Could not reach the server at …"; (f) Tray settings → **Forget saved admin password** → the next scout prompts again; (g) `%APPDATA%\DSoftCo\ConnectionWatcher\scout.json` contains no plaintext password; (h) **Scan this network** completes and the results appear on the Discoveries page with Origin `tray:<this PC>`.

- [ ] **Step 4: Commit only if something was fixed**

```bash
git status --short
git add -A -- Core Libraries Programs Tests deploy tools SECURITY.md
git commit -m "fix: address verification findings for the LAN scan and scout"
```

(Skip if clean. Do not push; hand the branch back for review before merging to `master`.)
