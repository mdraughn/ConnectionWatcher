using ConInfrastructure.Traceroute;
using ConPersistence.Settings;

namespace ConWatch.Api;

public static class TracerouteEndpoints
{
    public record TracerouteRequest(string Host);

    public static void MapTraceroute(this RouteGroupBuilder api)
    {
        api.MapPost("/traceroute", async (TracerouteRequest request, TracerouteRunner runner,
            SettingsStore settings, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "'host' is required.");
            var lookup = await settings.GetBoolAsync(SettingKeys.TracerouteAsnLookup);
            try
            {
                var hops = await runner.RunAsync(request.Host.Trim(), lookup, ct);
                return Results.Ok(new { Host = request.Host.Trim(), AsnLookupEnabled = lookup, Hops = hops });
            }
            catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, $"Cannot resolve '{request.Host}': {ex.Message}");
            }
        });
    }
}
