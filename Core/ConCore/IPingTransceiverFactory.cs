using System.Net;

namespace ConCore;

public interface IPingTransceiverFactory
{
    IPingTransceiver Create(IPAddress ipAddress, ProbeSpec probe, CancellationToken cancellationToken);
}