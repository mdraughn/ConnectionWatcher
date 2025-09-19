// See https://aka.ms/new-console-template for more information

using System.Net;
using ConEngine.Network;
using ConInfrastructure;

Console.WriteLine("Hello, World!");
var resolverFactory = new DomainNameResolverFactory();
using IDomainNameResolver resolver = resolverFactory.Create(CancellationToken.None);
string domainName = "cloudflare.com";
DomainResolutionResult hostInfo = await resolver.ResolveAsync(domainName, 5000);
foreach (IPAddress ipAddress in hostInfo.IPv4Addresses.Concat(hostInfo.IPv6Addresses))
{
    Console.WriteLine($"Resolved {domainName} IP: {ipAddress}");
}

var pingFactory = new PingTransceiverFactory();
var engine = new PingController(TimeProvider.System, pingFactory, CancellationToken.None);
engine.Start(IPAddress.Parse("8.8.8.8")).GetAwaiter().GetResult();

//using IPingTransceiver pt = pingFactory.Create(IPAddress.Parse("8.8.8.8"), CancellationToken.None);
//for (int i = 0; i < 5; i++)
//{
//    if (i > 0)
//    {
//        await Task.Delay(1000); // Wait 1 second between pings
//    }
//    PingResult result = await pt.PingAsync(5000);
//    Console.WriteLine($"Ping to {result.IpAddress} {(result.Success ? "succeeded" : "failed")} in {result.RoundtripTime} ms");
//}
