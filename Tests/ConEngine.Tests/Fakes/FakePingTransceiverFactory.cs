using System.Collections.Concurrent;
using System.Net;
using ConCore;

namespace ConEngine.Tests.Fakes;

/// <summary>
/// Instant, scriptable transceiver: per-IP up/down and throw switches.
/// Default behavior for unknown IPs: success with 10ms RTT.
/// </summary>
public class FakePingTransceiverFactory : IPingTransceiverFactory
{
    private readonly ConcurrentDictionary<IPAddress, bool> _up = new();
    private readonly ConcurrentDictionary<IPAddress, bool> _throw = new();

    /// <summary>Timeouts observed per ping, per IP (for burst-timeout assertions).</summary>
    public ConcurrentDictionary<IPAddress, ConcurrentQueue<int>> ObservedTimeouts { get; } = new();

    /// <summary>Probe spec seen at Create, per IP (for probe-threading assertions).</summary>
    public ConcurrentDictionary<IPAddress, ProbeSpec> ObservedProbes { get; } = new();

    /// <summary>Every transceiver created, in order (several per IP when targets share an address).</summary>
    public ConcurrentQueue<(IPAddress Ip, ProbeSpec Probe)> Created { get; } = new();

    private readonly ConcurrentDictionary<IPAddress, ConcurrentQueue<bool>> _scripts = new();
    private readonly ConcurrentDictionary<IPAddress, int> _dropEvery = new();
    private readonly ConcurrentDictionary<IPAddress, bool> _refused = new();
    private readonly ConcurrentDictionary<IPAddress, int> _counts = new();

    /// <summary>The next results from <paramref name="ip"/> follow the script; then the SetUp state applies.</summary>
    public void Script(IPAddress ip, params bool[] results)
    {
        var queue = _scripts.GetOrAdd(ip, _ => new());
        foreach (var r in results)
            queue.Enqueue(r);
    }

    /// <summary>Every n-th result from <paramref name="ip"/> fails; 0 turns it off.</summary>
    public void DropEveryNth(IPAddress ip, int n) => _dropEvery[ip] = n;

    /// <summary>Pings answered or thrown so far for <paramref name="ip"/>, across all its transceivers.</summary>
    public int PingCount(IPAddress ip) => _counts.GetValueOrDefault(ip);

    /// <summary>A failed-looking reply that still reports Success=true, with Message "Refused".</summary>
    public void SetRefused(IPAddress ip, bool refused) => _refused[ip] = refused;

    public void SetUp(IPAddress ip, bool up) => _up[ip] = up;
    public void SetThrow(IPAddress ip, bool throwOnPing) => _throw[ip] = throwOnPing;

    public IPingTransceiver Create(IPAddress ipAddress, ProbeSpec probe, CancellationToken cancellationToken)
    {
        ObservedProbes[ipAddress] = probe;
        Created.Enqueue((ipAddress, probe));
        return new FakeTransceiver(ipAddress, this);
    }

    private sealed class FakeTransceiver(IPAddress ip, FakePingTransceiverFactory owner) : IPingTransceiver
    {
        public IPAddress IpAddress => ip;

        public Task<PingResult> PingAsync(int timeoutMs)
        {
            owner.ObservedTimeouts.GetOrAdd(ip, _ => new()).Enqueue(timeoutMs);
            int count = owner._counts.AddOrUpdate(ip, 1, (_, c) => c + 1);
            if (owner._throw.GetValueOrDefault(ip))
                throw new InvalidOperationException("Simulated transceiver failure");
            if (owner._refused.GetValueOrDefault(ip))
                return Task.FromResult(new PingResult(ip, true, 0, "Refused"));
            bool up = owner._scripts.TryGetValue(ip, out var script) && script.TryDequeue(out var scripted)
                ? scripted
                : owner._up.GetValueOrDefault(ip, true);
            if (owner._dropEvery.GetValueOrDefault(ip) is > 0 and var n && count % n == 0)
                up = false;
            return Task.FromResult(new PingResult(ip, up, up ? 10 : 0, up ? null : "Timed out"));
        }

        public void Dispose() { }
    }
}
