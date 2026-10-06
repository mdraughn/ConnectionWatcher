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
        Func<IPAddress, CancellationToken, Task<string?>>? dns = null, int concurrency = LanScanner.MaxConcurrency,
        TimeSpan? nameBudget = null) =>
        new(nets, arp, new Vendors(), probe.Ping, dns ?? ((_, _) => Task.FromResult<string?>(null)), concurrency, nameBudget);

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
        Assert.Equal(LanScanner.MaxHosts, probe.Pinged.Count);
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

    [Fact]
    public void The_limits_are_pinned()
    {
        Assert.Equal(1024, LanScanner.MaxHosts);
        Assert.Equal(TimeSpan.FromSeconds(1), LanScanner.PingTimeout);
        Assert.Equal(64, LanScanner.MaxConcurrency);
    }

    [Fact]
    public async Task A_caller_asking_for_more_than_the_cap_still_gets_at_most_64_simultaneous_pings()
    {
        var current = 0;
        var peak = 0;
        var atCap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new LanScanner(new Networks(Net("192.168.1.50", 24)), new Arp(), new Vendors(),
            async (_, _) =>
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref peak, now);
                if (now >= LanScanner.MaxConcurrency)
                    atCap.TrySetResult();
                await atCap.Task;                 // hold until the cap's worth are in flight
                for (var i = 0; i < 5; i++)
                    await Task.Yield();           // give any over-limit starters a chance to show up
                Interlocked.Decrement(ref current);
                return null;
            },
            (_, _) => Task.FromResult<string?>(null), concurrency: 500);

        await scanner.ScanAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(LanScanner.MaxConcurrency, peak);
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
        var sixthArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new LanScanner(new Networks(Net("192.168.1.50", 24)),
            new Arp(new NeighborEntry("192.168.1.30", "aa:bb:cc:00:11:99")), new Vendors(),
            async (ip, ct) =>
            {
                if (Interlocked.Increment(ref answered) > 5)
                {
                    sixthArrived.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);   // the cap fires while these hang
                }
                return 2;
            },
            (_, _) => Task.FromResult<string?>(null), concurrency: 8);

        var scan = scanner.ScanAsync(cts.Token);
        await sixthArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));   // five answers have landed
        cts.Cancel();
        var result = await scan.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Complete);
        Assert.True(result.Found.Count >= 5, $"only {result.Found.Count} kept");
        Assert.Contains(result.Found, c => c.Address == "192.168.1.30");   // the ARP table is still read
    }

    [Fact]
    public async Task When_any_network_has_a_gateway_gateway_less_virtual_networks_are_not_swept()
    {
        var probe = new Probe();
        var nets = new Networks(
            Net("192.168.1.50", 24, "192.168.1.1"),     // the real LAN
            Net("172.20.0.1", 24),                       // a docker bridge
            Net("172.28.16.1", 24));                     // a WSL virtual switch

        await Scanner(nets, new Arp(), probe).ScanAsync(CancellationToken.None);

        Assert.Equal(253, probe.Pinged.Count);
        Assert.All(probe.Pinged, p => Assert.StartsWith("192.168.1.", p));
    }

    [Fact]
    public async Task When_no_network_has_a_gateway_all_private_networks_are_swept()
    {
        var probe = new Probe();
        var nets = new Networks(Net("192.168.1.50", 24), Net("10.9.9.5", 24));

        await Scanner(nets, new Arp(), probe).ScanAsync(CancellationToken.None);

        Assert.Equal(506, probe.Pinged.Count);
    }

    [Fact]
    public async Task A_ping_that_throws_for_one_host_is_just_no_answer()
    {
        var scanner = new LanScanner(new Networks(Net("192.168.1.50", 24)), new Arp(), new Vendors(),
            (ip, _) => ip.ToString() == "192.168.1.10" ? throw new InvalidOperationException("raw socket exploded")
                : Task.FromResult<long?>(ip.ToString() == "192.168.1.20" ? 3 : null),
            (_, _) => Task.FromResult<string?>(null));

        var result = await scanner.ScanAsync(CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal("192.168.1.20", Assert.Single(result.Found).Address);
    }

    [Fact]
    public async Task Name_lookups_after_a_cancelled_sweep_are_bounded_and_keep_the_names_resolved_so_far()
    {
        using var cts = new CancellationTokenSource();
        var hungPing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new LanScanner(new Networks(Net("192.168.1.50", 24)), new Arp(), new Vendors(),
            async (ip, ct) =>
            {
                var last = int.Parse(ip.ToString().Split('.')[3]);
                if (last is 20 or 21)
                    return 3;
                if (last > 21)
                {
                    hungPing.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);   // the cap fires while these hang
                }
                return null;
            },
            async (ip, ct) =>
            {
                if (ip.ToString() == "192.168.1.20")
                    return "nas.example.com";
                await Task.Delay(Timeout.Infinite, ct);       // only the name budget can end this one
                return null;
            },
            concurrency: 8, nameBudget: TimeSpan.FromMilliseconds(200));

        var scan = scanner.ScanAsync(cts.Token);
        await hungPing.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        var result = await scan.WaitAsync(TimeSpan.FromSeconds(30));   // would hang forever without the budget

        Assert.False(result.Complete);
        Assert.Equal("nas.example.com", result.Found.Single(c => c.Address == "192.168.1.20").Hostname);
        Assert.Null(result.Found.Single(c => c.Address == "192.168.1.21").Hostname);
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
