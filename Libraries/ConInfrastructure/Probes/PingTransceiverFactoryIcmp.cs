using System.Net;
using ConCore;
using ConCore.Dns;
using ConInfrastructure.NameResolution;

namespace ConInfrastructure.Probes;

/// <summary>The production transceivers: ICMP echo, TCP connect, or a DNS query
/// that asks the host-maintained lookup names (none given: every DNS probe
/// reports "NoLookupNames").</summary>
public class PingTransceiverFactoryIcmp(IDnsLookupNames? lookupNames = null) : IPingTransceiverFactory
{
    private readonly IDnsLookupNames _lookupNames = lookupNames ?? DnsLookupNames.Empty;

    public IPingTransceiver Create(IPAddress ipAddress, ProbeSpec probe, CancellationToken cancellationToken) =>
        probe.Kind switch
        {
            ProbeKind.Tcp => new TcpConnectTransceiver(ipAddress, probe.Port, cancellationToken),
            ProbeKind.Dns => new DnsQueryTransceiver(ipAddress, _lookupNames, cancellationToken),
            _ => new PingTransceiverIcmp(ipAddress, cancellationToken),
        };
}
