using System.Net;
using System.Net.Sockets;

namespace ConWatchCli.Services;

public sealed class TcpPortProbe : IPortProbe
{
    public bool IsFree(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }
}
