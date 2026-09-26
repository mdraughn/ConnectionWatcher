using ConInfrastructure.Traceroute;

namespace ConWatch.Api;

public static class TracerouteEndpoints
{
    public record TracerouteRequest(string Host);

    public static void MapTraceroute(this RouteGroupBuilder api)
    {
        api.MapPost("/traceroute", async (TracerouteRequest request, TracerouteRunner runner,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Host))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, "'host' is required.");
            try
            {
                var hops = await runner.RunAsync(request.Host.Trim(), ct);
                return Results.Ok(new { Host = request.Host.Trim(), Hops = hops });
            }
            catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
            {
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, $"Cannot resolve '{request.Host}': {ex.Message}");
            }
        });
    }
}
