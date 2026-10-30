using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ConInfrastructure.Net;

namespace ConInfrastructure.Discovery;

/// <summary>
/// Asks the router for its external IP via UPnP-IGD: SSDP discovery, device
/// description fetch, then a GetExternalIPAddress SOAP call. Every step is
/// time-bounded; any failure yields a reason instead of an exception. On
/// success the address is test-pinged (hairpin check) before being offered.
/// </summary>
public partial class UpnpWanAddressProbe : IWanAddressProbe
{
    private static readonly string[] WanServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    public async Task<WanProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var location = await SsdpDiscoverAsync(ct).ConfigureAwait(false);
            if (location is null)
                return new WanProbeResult(null, "No UPnP internet-gateway device answered on the network.", false, null);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var descriptionXml = await http.GetStringAsync(location, ct).ConfigureAwait(false);
            var (serviceType, controlUrl) = FindWanService(descriptionXml, location);
            if (controlUrl is null)
                return new WanProbeResult(null, "The gateway exposes no WAN connection service over UPnP.", false, null);

            var address = await GetExternalIpAsync(http, serviceType!, controlUrl, ct).ConfigureAwait(false);
            if (address is null || address is "0.0.0.0" or "")
                return new WanProbeResult(null, "The gateway did not report an external address.", false, null);

            var (pingOk, rtt) = await TestPingAsync(address, ct).ConfigureAwait(false);
            return new WanProbeResult(address, null, pingOk, rtt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WanProbeResult(null, $"UPnP probe failed: {ex.Message}", false, null);
        }
    }

    private static async Task<string?> SsdpDiscoverAsync(CancellationToken ct)
    {
        const string searchTarget = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
        var request = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\n" +
                      $"MAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {searchTarget}\r\n\r\n";
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        var payload = Encoding.ASCII.GetBytes(request);
        await udp.SendAsync(payload, payload.Length,
            new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900)).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var response = Encoding.ASCII.GetString(result.Buffer);
                var match = LocationHeader().Match(response);
                if (match.Success)
                    return match.Groups[1].Value.Trim();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // discovery window elapsed
        }
    }

    private static (string? ServiceType, string? ControlUrl) FindWanService(string descriptionXml, string baseUrl)
    {
        var doc = XDocument.Parse(descriptionXml);
        foreach (var service in doc.Descendants().Where(e => e.Name.LocalName == "service"))
        {
            var type = service.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value;
            var control = service.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value;
            if (type is not null && control is not null && WanServiceTypes.Contains(type))
                return (type, new Uri(new Uri(baseUrl), control).ToString());
        }
        return (null, null);
    }

    private static async Task<string?> GetExternalIpAsync(HttpClient http, string serviceType,
        string controlUrl, CancellationToken ct)
    {
        var soap = "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>" +
            $"<u:GetExternalIPAddress xmlns:u=\"{serviceType}\"/></s:Body></s:Envelope>";
        using var content = new StringContent(soap, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPACTION", $"\"{serviceType}#GetExternalIPAddress\"");
        var response = await http.PostAsync(controlUrl, content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return XDocument.Parse(body).Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
    }

    private static async Task<(bool Ok, long? RttMs)> TestPingAsync(string address, CancellationToken ct)
    {
        if (!IPAddress.TryParse(address, out var ip))
            return (false, null);
        var rtt = await IcmpEcho.TryPingAsync(ip, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        return (rtt is not null, rtt);
    }

    [GeneratedRegex(@"(?im)^LOCATION:\s*(\S+)\s*$")]
    private static partial Regex LocationHeader();
}
