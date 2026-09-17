using System.Net;
using ConCore;

namespace ConInfrastructure;

public class PingTransceiverFactoryIcmp : IPingTransceiverFactory
{
    public IPingTransceiver Create(IPAddress ipAddress, ProbeSpec probe, CancellationToken cancellationToken) =>
        probe.Kind switch
        {
            ProbeKind.Tcp => new TcpConnectTransceiver(ipAddress, probe.Port, cancellationToken),
            _ => new PingTransceiverIcmp(ipAddress, cancellationToken),
        };
}