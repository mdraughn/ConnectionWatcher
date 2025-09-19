using System.Net;
using ConEngine.Network;

namespace ConInfrastructure;

public class PingTransceiverFactory : IPingTransceiverFactory
{
    public IPingTransceiver Create(IPAddress ipAddress, CancellationToken cancellationToken)
    {
        return new PingTranceiverIcmp(ipAddress, cancellationToken);
    }
}