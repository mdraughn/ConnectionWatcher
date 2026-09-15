using System.Net;
using ConCore;

namespace ConInfrastructure;

public class PingTransceiverFactoryIcmp : IPingTransceiverFactory
{
    public IPingTransceiver Create(IPAddress ipAddress, ProbeSpec probe, CancellationToken cancellationToken) =>
        probe.Kind switch
        {
            ProbeKind.Tcp => throw new NotSupportedException("TCP probe arrives in the next commit"),
            _ => new PingTranceiverIcmp(ipAddress, cancellationToken),
        };
}