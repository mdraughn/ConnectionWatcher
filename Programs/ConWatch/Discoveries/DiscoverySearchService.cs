using System.Net.Sockets;
using ConInfrastructure.Discovery;
using ConPersistence.Discoveries;

namespace ConWatch.Discoveries;

/// <param name="Busy">Another search holds the gate.</param>
/// <param name="TimedOut">The search ran past the cap; nothing was stored.</param>
public sealed record SearchOutcome(bool Busy, bool TimedOut, int Found, int Created, int Updated,
    string? Warning, IReadOnlyList<string> Errors)
{
    public static SearchOutcome IsBusy { get; } = new(true, false, 0, 0, 0, null, []);
}

/// <summary>
/// Runs the node's own searches one at a time with an overall time cap, and files
/// what they find in the inbox with Origin "server". The cap and the gate are what
/// keep a stuck traceroute from wedging the page.
/// </summary>
public sealed class DiscoverySearchService(IDiscoveryScanner scanner, DiscoveryStore store,
    ILogger<DiscoverySearchService> log, TimeSpan? cap = null)
{
    public const string ServerOrigin = "server";
    public static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _cap = cap ?? DefaultCap;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<SearchOutcome> SearchRouterAsync(CancellationToken ct) =>
        RunAsync(t => scanner.FindRouterAsync(t), ct);

    /// <param name="host">The public address to trace toward; null or blank uses <see cref="DiscoveryScanner.DefaultEdgeHost"/>.</param>
    public Task<SearchOutcome> SearchEdgeAsync(string? host, CancellationToken ct)
    {
        var target = string.IsNullOrWhiteSpace(host) ? DiscoveryScanner.DefaultEdgeHost : host.Trim();
        return RunAsync(t => scanner.FindNetworkEdgeAsync(target, t), ct);
    }

    private async Task<SearchOutcome> RunAsync(Func<CancellationToken, Task<IReadOnlyList<DiscoveryCandidate>>> search,
        CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
            return SearchOutcome.IsBusy;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_cap);
            IReadOnlyList<DiscoveryCandidate> found;
            try
            {
                found = await search(cts.Token);
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

            if (found.Count == 0)
                return new SearchOutcome(false, false, 0, 0, 0, Warning(), []);
            var stored = await store.UpsertAsync(found.Select(c => new DiscoveryInput(
                c.Address, c.Hostname, c.Kind, c.Method, ServerOrigin, c.Evidence)).ToList());
            return stored.Success
                ? new SearchOutcome(false, false, found.Count, stored.Created, stored.Updated, Warning(), [])
                : new SearchOutcome(false, false, 0, 0, 0, null, stored.Errors);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? Warning() => ContainerHint.IsLikelyContainer() ? ContainerHint.Warning : null;
}
