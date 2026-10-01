using System.Collections.Concurrent;
using System.Net;
using ConCore;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.Extensions.Logging;

namespace ConInfrastructure.Asn;

/// <summary>
/// IP → ASN through Team Cymru's DNS service, via the system resolver, with
/// a 2-second cap per query and a per-process cache (24 h for answers,
/// 10 min for misses). Private and carrier addresses are never queried.
/// </summary>
public sealed class CymruAsnLookup(ILogger<CymruAsnLookup> log, TimeProvider? timeProvider = null) : IAsnLookup
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<IPAddress, (AsnInfo? Info, DateTimeOffset Expires)> _cache = new();
    private readonly LookupClient _dns = new(new LookupClientOptions
    {
        Timeout = TimeSpan.FromSeconds(2),
        Retries = 0,
        UseCache = true,
    });

    public async Task<AsnInfo?> LookupAsync(IPAddress address, CancellationToken ct)
    {
        if (ProbePolicy.IsPrivateOrCarrier(address))
            return null;
        if (_cache.TryGetValue(address, out var cached) && cached.Expires > _time.GetUtcNow())
            return cached.Info;

        AsnInfo? info = null;
        try
        {
            var asn = CymruResponseParser.ParseOrigin(
                await TxtAsync(CymruResponseParser.OriginQueryName(address), ct).ConfigureAwait(false));
            if (asn is { } number)
            {
                var named = CymruResponseParser.ParseAsName(
                    await TxtAsync(CymruResponseParser.AsQueryName(number), ct).ConfigureAwait(false));
                info = new AsnInfo(number, named?.Name ?? $"AS{number}", named?.CountryCode);
            }
        }
        catch (Exception ex) when (ex is DnsResponseException or OperationCanceledException or TimeoutException)
        {
            log.LogDebug(ex, "ASN lookup for {Address} failed", address);
        }

        _cache[address] = (info, _time.GetUtcNow() + (info is null ? MissTtl : HitTtl));
        return info;
    }

    private async Task<string?> TxtAsync(string name, CancellationToken ct)
    {
        var result = await _dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct).ConfigureAwait(false);
        var record = result.Answers.TxtRecords().FirstOrDefault();
        return record is null ? null : string.Concat(record.Text);
    }
}
