// See https://aka.ms/new-console-template for more information

using System.Net;
using ConEngine.Network;
using ConInfrastructure;

Console.WriteLine("Hello, World!");

using IPingTarget pt = new PingTargetIpImpl(IPAddress.Parse("8.8.8.8"), CancellationToken.None);
for (int i = 0; i < 5; i++)
{
    if (i > 0)
    {
        await Task.Delay(1000); // Wait 1 second between pings
    }
    PingResult result = await pt.PingAsync(5000);
    Console.WriteLine($"Ping to {result.IpAddress} {(result.Success ? "succeeded" : "failed")} in {result.RoundtripTime} ms");
}
