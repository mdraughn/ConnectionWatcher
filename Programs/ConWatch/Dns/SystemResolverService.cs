using System.Net;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using ConCore.Dns;
using ConCore.Targets;
using ConInfrastructure.NameResolution;
using ConPersistence.Config;
using ConPersistence.Settings;

namespace ConWatch.NameResolution;

/// <summary>
/// Keeps one system-managed Resolver target per resolver the host is
/// configured to use (spec 2026-10-10 §4.3, D7). Reconciles at start, every
/// <see cref="Interval"/>, when the OS network addresses change, when the config
/// changes (a hostname target can activate the watch) and when a Dns.* setting changes. Every change goes through ConfigService, which
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
        config.ConfigChanged += OnConfigChanged;
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

                try
                {
                    await WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            settings.SettingsChanged -= OnSettingsChanged;
            config.ConfigChanged -= OnConfigChanged;
        }
    }

    /// <summary>
    /// Sleeps until the interval elapses or a wake is signalled. A wake signalled
    /// before this call is still seen (the bounded channel keeps one pending
    /// item and WaitToReadAsync completes at once). Both waits are cancelled and
    /// awaited so neither leaves an unobserved TaskCanceledException behind.
    /// </summary>
    private async Task WaitAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(Interval, time, cts.Token);
        var wake = _wake.Reader.WaitToReadAsync(cts.Token).AsTask();
        await Task.WhenAny(delay, wake);
        await cts.CancelAsync();
        try
        {
            await Task.WhenAll(delay, wake);
        }
        catch (OperationCanceledException)
        {
            // The loser of the race (or shutdown) — observed here.
        }
        ct.ThrowIfCancellationRequested();
        _wake.Reader.TryRead(out _); // coalesce: one reconcile covers every wake so far
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _wake.Writer.TryWrite(true);

    // Any config edit may add a hostname target (which makes the watch active) or a hand-made
    // dns target; a reconcile that finds nothing to do is a no-op, so this costs one per edit.
    private void OnConfigChanged() => _wake.Writer.TryWrite(true);

    private void OnSettingsChanged(IReadOnlyDictionary<string, string?> written)
    {
        if (written.Keys.Any(k => k is SettingKeys.DnsEnabled or SettingKeys.DnsLookupNames))
            _wake.Writer.TryWrite(true);
    }

    /// <summary>
    /// One reconcile, serialized. True when the stored config changed.
    /// The config commit and the Dns.RetiredResolvers write are two separate
    /// transactions, so the order keeps the retired set a SUPERSET at every
    /// failure point: (1) plan on a snapshot; (2) before committing, write the
    /// union of the stored and planned retired sets; (3) commit the config;
    /// (4) after the commit, narrow the stored set to exactly the plan's.
    /// A crash after (2) leaves a harmless extra entry (the reconciler forgets
    /// it once that target is enabled and present); it can never leave a
    /// reconciler-disabled target missing from the set, which would make the
    /// target look admin-disabled and never be restored.
    /// </summary>
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

            SystemResolverReconciler.Plan PlanFor(ConfigDocument doc)
            {
                // Active: the watch is on and there is something to ask for — the
                // configured names, or any hostname target the resolver must resolve anyway.
                var active = enabled && (names.Count > 0 || doc.Targets.Any(t =>
                    t.Enabled && t.Scope != TargetScope.Resolver && !IPAddress.TryParse(t.Address, out _)));
                return SystemResolverReconciler.Reconcile(doc.Targets, found, active, retired);
            }

            var preview = PlanFor(await store.ExportAsync());
            if (!preview.Changed)
            {
                LogOnceWhenChanged(preview.Log);
                return false;
            }
            if (!preview.Retired.IsSubsetOf(retired))
                await WriteRetiredAsync(retired.Union(preview.Retired, StringComparer.OrdinalIgnoreCase));

            SystemResolverReconciler.Plan? plan = null;
            var result = await config.EditDocumentAsync(doc =>
            {
                plan = PlanFor(doc);
                // A failed edit commits nothing, so "no change" is expressed as a
                // non-success result; the caller checks plan.Changed before the result.
                if (!plan.Changed)
                    return ConfigResult.Fail(ConfigErrorKind.None, "no change");
                doc.Targets.Clear();
                doc.Targets.AddRange(plan.Targets);
                return ConfigResult.Ok();
            });

            if (plan is null || !plan.Changed)
                return false;
            if (!result.Success)
            {
                log.LogWarning("System resolver reconcile rejected: {Errors}", string.Join("; ", result.Errors ?? []));
                return false; // the pre-written union stays: harmless
            }
            var stored = preview.Retired.IsSubsetOf(retired) ? retired : retired.Union(preview.Retired).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!plan.Retired.SetEquals(stored))
                await WriteRetiredAsync(plan.Retired);
            foreach (var line in plan.Log)
                log.LogInformation("System resolvers: {Change}", line);
            return true;
        }
        finally
        {
            _once.Release();
        }
    }

    private string _lastSkipLog = "";

    // A skip repeats on every reconcile (5 min, each config edit); say it again only when it changes.
    private void LogOnceWhenChanged(IReadOnlyList<string> lines)
    {
        var text = string.Join("; ", lines);
        if (text == _lastSkipLog)
            return;
        _lastSkipLog = text;
        foreach (var line in lines)
            log.LogInformation("System resolvers: {Change}", line);
    }

    private Task WriteRetiredAsync(IEnumerable<string> addresses)
    {
        var sorted = addresses.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return settings.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.DnsRetiredResolvers] = sorted.Count == 0 ? null : string.Join('\n', sorted),
        });
    }

    private static HashSet<string> Parse(string? value) =>
        new((value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
}
