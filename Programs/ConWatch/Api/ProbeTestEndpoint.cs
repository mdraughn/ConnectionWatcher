using System.Net;
using ConCore;

namespace ConWatch.Api;

/// <summary>One-click probe discovery for the Targets form: the SERVER
/// (where monitoring runs) tries ICMP plus the policy-allowed TCP ports
/// once each, so the form can preselect a probe that actually answers.</summary>
public static class ProbeTestEndpoint
{
    public record Request(string Address, int? Port = null);
    private const int TimeoutMs = 2000;

    public static void MapProbeTest(this RouteGroupBuilder locked) =>
        locked.MapPost("/targets/probe-test", async (Request request,
            IPingTransceiverFactory factory, IDomainNameResolverFactory resolvers,
            CancellationToken ct) =>
        {
            IPAddress? ip = null;
            if (!IPAddress.TryParse(request.Address, out ip))
            {
                using var resolver = resolvers.Create(ct);
                var resolution = await resolver.ResolveAsync(request.Address, 5000);
                ip = resolution.IPv4Addresses.FirstOrDefault();
            }
            if (ip is null)
                return Results.Ok(new { request.Address, ResolvedIp = (string?)null,
                    Results = Array.Empty<object>() });

            var ports = (ProbePolicy.IsPrivate(request.Address)
                    ? new[] { 80, 443, 8080, 8443 } : ProbePolicy.PublicPorts)
                .Concat(request.Port is { } p &&
                    ProbePolicy.IsAllowed(request.Address, new ProbeSpec(ProbeKind.Tcp, p), out _)
                    ? [p] : Array.Empty<int>())
                .Distinct().ToList();
            var candidates = new List<ProbeSpec> { ProbeSpec.Icmp };
            candidates.AddRange(ports.Select(port => new ProbeSpec(ProbeKind.Tcp, port)));

            var results = await Task.WhenAll(candidates.Select(async probe =>
            {
                using var transceiver = factory.Create(ip, probe, ct);
                var result = await transceiver.PingAsync(TimeoutMs);
                return new { Probe = probe.ToString(), Reply = result.Success,
                    RttMs = result.RoundtripTime, Message = result.Message };
            }));
            return Results.Ok(new { request.Address, ResolvedIp = ip.ToString(), Results = results });
        });
}
