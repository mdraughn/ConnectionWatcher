using ConInfrastructure.Discovery;

namespace ConWatch.Api;

/// <summary>Quick-setup wizard backend: discovery + additive apply.</summary>
public static class SetupEndpoints
{
    public static void MapSetup(this RouteGroupBuilder api)
    {
        api.MapGet("/setup/gateway", async (IGatewayDiscovery discovery, CancellationToken ct) =>
            await discovery.DiscoverAsync(ct));

        api.MapGet("/setup/anchors", () => SetupService.AnchorCatalog);

        api.MapPost("/setup/wan-probe", async (IWanAddressProbe probe, CancellationToken ct) =>
            await probe.ProbeAsync(ct));

        api.MapPost("/setup/apply", async (SetupApplyRequest request, SetupService setup) =>
        {
            if (string.IsNullOrWhiteSpace(request.Router?.Address))
                return Results.BadRequest(new { errors = new[] { "A router address is required." } });
            return (await setup.ApplyAsync(request)).ToHttp();
        });
    }
}
