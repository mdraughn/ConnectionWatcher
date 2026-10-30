using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ConConfig.Tests;

/// <summary>
/// What trying ::1 first costs when nothing listens there: ~2 s of SYN retries
/// on Windows, next to nothing on Linux. A client that waits on ::1 before
/// reaching an IPv4-only server cannot answer faster than this, however idle
/// the machine — so it is the bound to beat, where a fixed wall-clock limit
/// fails on a loaded two-core runner.
/// </summary>
internal static class Ipv6Detour
{
    /// <summary>Below this there is no detour worth detecting (Linux refuses at once).</summary>
    public const long DetectableMs = 500;

    public static async Task<long> MeasureMsAsync(int port)
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        var watch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.IPv6Loopback, port));
        }
        catch (SocketException)
        {
            // Refused (or no IPv6 at all): either way, the time is the cost.
        }
        return watch.ElapsedMilliseconds;
    }
}
