using ConCore.Discovery;
using ConInfrastructure.Asn;
using ConInfrastructure.Discovery;
using ConInfrastructure.Tray;
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
                    found = await Task.Run(() => _scanner.FindRouterAsync(cts.Token));
                    break;
                case ScoutKind.NetworkEdge:
                    found = await Task.Run(() => _scanner.FindNetworkEdgeAsync(DiscoveryScanner.DefaultEdgeHost, cts.Token));
                    break;
                default:
                    var lan = await Task.Run(() => _scanner.ScanLanAsync(cts.Token));
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
