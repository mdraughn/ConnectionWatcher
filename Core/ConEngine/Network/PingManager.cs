using System.Net;
using System.Runtime.CompilerServices;

namespace ConEngine.Network;

public class PingManager(TimeProvider timeProvider,
    IPingTransceiverFactory pingTransceiverFactory,
    CancellationToken cancellationToken)
{
    private readonly Dictionary<IPAddress,PingController> _pingControllers = new Dictionary<IPAddress,PingController>();

    public PingController GetOrCreateController(IPAddress ipAddress)
    {
        if (!_pingControllers.TryGetValue(ipAddress, out var controller))
        {
            controller = new PingController(timeProvider, pingTransceiverFactory, cancellationToken);
            _pingControllers[ipAddress] = controller;
        }
        return controller;
    }
}