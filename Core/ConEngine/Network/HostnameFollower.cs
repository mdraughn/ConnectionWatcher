using System.Collections.Concurrent;
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Network;

/// <summary>
/// Keeps hostname targets pointed at a current address: one <see cref="FollowAsync"/>
/// loop per target, plus <see cref="RequestRefresh"/> to wake a loop early.
/// </summary>
internal sealed class HostnameFollower
{
    private readonly IDomainNameResolverFactory _resolverFactory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _refreshInterval;
    private readonly ILogger _log;
    private readonly Action<TargetMonitor, IPAddress> _activate;
    private readonly Func<TargetMonitor, IPAddress, Task> _repoint;

    /// <summary>Per hostname target: completing the source wakes its DNS refresh loop early.</summary>
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _dnsRefreshRequests = new();

    public HostnameFollower(IDomainNameResolverFactory resolverFactory, TimeProvider time,
        TimeSpan refreshInterval, ILogger log, Action<TargetMonitor, IPAddress> activate,
        Func<TargetMonitor, IPAddress, Task> repoint)
    {
        _resolverFactory = resolverFactory;
        _time = time;
        _refreshInterval = refreshInterval;
        _log = log;
        _activate = activate;
        _repoint = repoint;
    }

    /// <summary>Wakes the target's refresh loop now, if it is waiting.</summary>
    public void RequestRefresh(Guid targetId)
    {
        if (_dnsRefreshRequests.TryGetValue(targetId, out var request))
            request.TrySetResult();
    }

    /// <summary>
    /// Owns a hostname target for the engine's lifetime: resolves (with backoff
    /// until the first answer), activates, then keeps re-resolving every
    /// the refresh interval and immediately when the target goes
    /// Down. The current address is kept while it is still in the answer set;
    /// when it drops out the target is re-pointed at the first address. A
    /// failed refresh keeps the current address.
    /// </summary>
    public async Task FollowAsync(TargetMonitor monitor, string hostname, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(30);
        var maxBackoff = TimeSpan.FromMinutes(5);
        using var resolver = _resolverFactory.Create(ct);
        IPAddress? current = null;

        while (!ct.IsCancellationRequested)
        {
            List<IPAddress> answers = [];
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

            try
            {
                if (current is null)
                {
                    if (answers.Count == 0)
                    {
                        // Not activated yet: retry with backoff until DNS answers.
                        try { await Task.Delay(backoff, _time, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
                        continue;
                    }
                    // Adopt the answer only once the call it feeds has
                    // returned: the catch below promises a retry at the next
                    // refresh, which it can only keep if a throw leaves
                    // `current` as it was.
                    var next = answers[0];
                    _activate(monitor, next);
                    current = next;
                }
                else if (answers.Count > 0 && !answers.Contains(current))
                {
                    _log.LogInformation("{Target}: {Old} left the DNS answer set; re-pointing to {New}",
                        monitor.Target.Name, current, answers[0]);
                    var next = answers[0];
                    await _repoint(monitor, next).ConfigureAwait(false);
                    current = next;
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

            // Sleep until the refresh interval or a Down transition, whichever first.
            var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _dnsRefreshRequests[monitor.Target.Id] = request;
            try
            {
                await Task.WhenAny(Task.Delay(_refreshInterval, _time, ct), request.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
