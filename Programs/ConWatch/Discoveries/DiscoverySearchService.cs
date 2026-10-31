using System.Net.Sockets;
using ConCore.Discovery;
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
/// Runs the node's own searches one at a time with an overall time cap, and files
/// what they find in the inbox with Origin "server". The cap and the gate are what
/// keep a stuck traceroute from wedging the page.
/// </summary>
public sealed class DiscoverySearchService(IDiscoveryScanner scanner, DiscoveryStore store,
    ILogger<DiscoverySearchService> log, TimeSpan? cap = null, Func<bool>? inContainer = null)
{
    public const string ServerOrigin = "server";
    public static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _cap = cap ?? DefaultCap;
    private readonly Func<bool> _inContainer = inContainer ?? ContainerHint.IsLikelyContainer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<SearchOutcome> SearchRouterAsync(CancellationToken ct) =>
        RunAsync(async t => (await scanner.FindRouterAsync(t), true), ct, lan: false);

    /// <param name="host">The public address to trace toward; null or blank uses <see cref="DiscoveryScanner.DefaultEdgeHost"/>.</param>
    public Task<SearchOutcome> SearchEdgeAsync(string? host, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(host) ? DiscoveryScanner.DefaultEdgeHost : host.Trim();
        return RunAsync(async t => (await scanner.FindNetworkEdgeAsync(target, t), true), ct, lan: false);
    }

    /// <summary>The LAN scan keeps its partial results when the cap cuts it short.</summary>
    public Task<SearchOutcome> SearchLanAsync(CancellationToken ct) =>
        RunAsync(async t =>
        {
            var scan = await scanner.ScanLanAsync(t);
            return (scan.Found, scan.Complete);
        }, ct, lan: true);

    private async Task<SearchOutcome> RunAsync(
        Func<CancellationToken, Task<(IReadOnlyList<DiscoveryCandidate> Found, bool Complete)>> search,
        CancellationToken ct, bool lan)
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
                return new SearchOutcome(false, true, 0, 0, 0, Warning(lan), []);
            }
            catch (Exception ex) when (ex is ArgumentException or SocketException)
            {
                return new SearchOutcome(false, false, 0, 0, 0, null, [$"Cannot search: {ex.Message}"]);
            }
            // The client left: a scan that swallowed the cancellation and returned a partial list is not "done".
            ct.ThrowIfCancellationRequested();

            if (found.Count == 0)
                return new SearchOutcome(false, !complete, 0, 0, 0, Warning(lan), [], Partial: false);
            // A LAN scan can find more than one store batch holds; file it in batch-sized pieces.
            int created = 0, updated = 0;
            foreach (var chunk in found.Chunk(DiscoveryLimits.MaxBatch))
            {
                var stored = await store.UpsertAsync(chunk.Select(c => new DiscoveryInput(
                    c.Address, c.Hostname, c.Kind, c.Method, ServerOrigin, c.Evidence)).ToList());
                if (!stored.Success)
                    return new SearchOutcome(false, false, 0, 0, 0, null, stored.Errors);
                created += stored.Created;
                updated += stored.Updated;
            }
            return new SearchOutcome(false, false, found.Count, created, updated, Warning(lan), [], Partial: !complete);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Warning(bool lan) => _inContainer() ? (lan ? ContainerHint.LanWarning : ContainerHint.Warning) : null;
}
