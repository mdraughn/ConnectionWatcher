using System.Net;

namespace ConEngine.Network;

public interface IPingTransceiverFactory
{
    IPingTransceiver Create(IPAddress ipAddress, CancellationToken cancellationToken);
}