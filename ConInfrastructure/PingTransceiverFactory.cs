using System.Net;
using ConCore;

namespace ConInfrastructure;

public class PingTransceiverFactory : IPingTransceiverFactory
{
    public IPingTransceiver Create(IPAddress ipAddress, CancellationToken cancellationToken)
    {
        return new PingTranceiverIcmp(ipAddress, cancellationToken);
    }
}