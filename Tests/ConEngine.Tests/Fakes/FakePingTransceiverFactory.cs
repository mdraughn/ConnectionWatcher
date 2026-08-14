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

    public void SetUp(IPAddress ip, bool up) => _up[ip] = up;
    public void SetThrow(IPAddress ip, bool throwOnPing) => _throw[ip] = throwOnPing;

    public IPingTransceiver Create(IPAddress ipAddress, CancellationToken cancellationToken) =>
        new FakeTransceiver(ipAddress, this);

    private sealed class FakeTransceiver(IPAddress ip, FakePingTransceiverFactory owner) : IPingTransceiver
    {
        public IPAddress IpAddress => ip;

        public Task<PingResult> PingAsync(int timeoutMs)
        {
            if (owner._throw.GetValueOrDefault(ip))
                throw new InvalidOperationException("Simulated transceiver failure");
            bool up = owner._up.GetValueOrDefault(ip, true);
            return Task.FromResult(new PingResult(ip, up, up ? 10 : 0, up ? null : "Timed out"));
        }

        public void Dispose() { }
    }
}
