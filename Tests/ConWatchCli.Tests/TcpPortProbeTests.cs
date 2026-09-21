using System.Net;
using System.Net.Sockets;
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class TcpPortProbeTests
{
    [Fact]
    public void A_listening_port_is_busy_and_a_released_one_is_free()
    {
        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var probe = new TcpPortProbe();
        Assert.False(probe.IsFree(port));
        listener.Stop();
        Assert.True(probe.IsFree(port));
    }
}
