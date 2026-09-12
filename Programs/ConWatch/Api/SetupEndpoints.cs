using ConInfrastructure.Discovery;
using ConWatch.Clone;

namespace ConWatch.Api;

/// <summary>Quick-setup wizard backend: discovery + additive apply + clone.</summary>
public static class SetupEndpoints
{
    public record CloneRequest(string? SourceUrl, string? SourcePassword,
        bool IncludeSecrets = true, bool AllowInsecure = false);

    public record TakeoverRequest(string? SourcePassword);

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

        api.MapPost("/setup/clone", async (CloneRequest request, CloneService clone,
            CancellationToken ct) => ToHttp(await clone.CloneAsync(request.SourceUrl,
                request.SourcePassword, request.IncludeSecrets, request.AllowInsecure, ct)));

        api.MapPost("/setup/takeover", async (TakeoverRequest request, CloneService clone,
            CancellationToken ct) => ToHttp(await clone.TakeOverAsync(request.SourcePassword, ct)));
    }

    private static IResult ToHttp(CloneOutcome outcome) => outcome.Status switch
    {
        200 => Results.Ok(outcome.Body),
        401 => Results.Json(new { errors = new[] { outcome.Error }, code = outcome.ErrorCode },
            statusCode: 401),
        _ => Results.Json(new { errors = new[] { outcome.Error }, code = outcome.ErrorCode },
            statusCode: outcome.Status),
    };
}
