using ConInfrastructure.Discovery;
using ConWatch.Clone;

namespace ConWatch.Api;

/// <summary>Quick-setup wizard backend: discovery + additive apply + clone.</summary>
public static class SetupEndpoints
{
    public record CloneRequest(string? SourceUrl, string? SourcePassword,
        bool IncludeSecrets = true, bool AllowInsecure = false);

    public record TakeoverRequest(string? SourcePassword, bool AllowInsecure = false);

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
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "A router address is required.");
            return (await setup.ApplyAsync(request)).ToHttp();
        });

        api.MapPost("/setup/clone", async (CloneRequest request, CloneService clone,
            CancellationToken ct) => (await clone.CloneAsync(request.SourceUrl,
                request.SourcePassword, request.IncludeSecrets, request.AllowInsecure, ct)).ToHttp());

        api.MapPost("/setup/takeover", async (TakeoverRequest request, CloneService clone,
            CancellationToken ct) => (await clone.TakeOverAsync(request.SourcePassword,
                request.AllowInsecure, ct)).ToHttp());
    }
}
