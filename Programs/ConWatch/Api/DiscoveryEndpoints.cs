using ConPersistence.Discoveries;
using ConWatch.Discoveries;

namespace ConWatch.Api;

/// <summary>The Discoveries inbox. Everything here is admin-gated (mapped on the locked group);
/// the MCP list_discoveries tool follows the same gate.</summary>
public static class DiscoveryEndpoints
{
    public record UpsertRequest(List<DiscoveryInput>? Items);

    public record EdgeSearchRequest(string? Host);

    public static void MapDiscoveries(this RouteGroupBuilder locked)
    {
        var d = locked.MapGroup("/discoveries");

        d.MapGet("/", async (DiscoveryStore store, string? status) =>
            DiscoveryStatuses.TryParseFilter(status, out var filter)
                ? Results.Ok(await store.ListAsync(filter))
                : ApiResults.Error(StatusCodes.Status400BadRequest, null,
                    "'status' must be new, hidden, adopted or all."));

        d.MapPost("/", async (UpsertRequest request, DiscoveryStore store) =>
        {
            var result = await store.UpsertAsync((IReadOnlyList<DiscoveryInput>?)request.Items ?? []);
            return result.Success
                ? Results.Ok(new { created = result.Created, updated = result.Updated })
                : ApiResults.Error(StatusCodes.Status400BadRequest, null, result.Errors.ToArray());
        });

        d.MapPost("/search/router", async (DiscoverySearchService search, CancellationToken ct) =>
            ToHttp(await search.SearchRouterAsync(ct)));

        d.MapPost("/search/lan", async (DiscoverySearchService search, CancellationToken ct) =>
            ToHttp(await search.SearchLanAsync(ct)));

        d.MapPost("/search/edge", async (EdgeSearchRequest? request, DiscoverySearchService search,
            CancellationToken ct) => ToHttp(await search.SearchEdgeAsync(request?.Host, ct)));

        d.MapPost("/restore-dismissed", async (DiscoveryStore store) =>
            Results.Ok(new { restored = await store.RestoreAllDismissedAsync() }));

        d.MapPost("/clear", async (string? kind, DiscoveryStore store) =>
            TryParseKind(kind, out var parsed)
                ? Results.Ok(new { removed = await store.ClearAsync(parsed) })
                : ApiResults.Error(StatusCodes.Status400BadRequest, null,
                    "'kind' is required and must be NetworkEdge, LocalDevice or Gateway."));

        d.MapDelete("/{id:guid}", async (Guid id, DiscoveryStore store) =>
            (await store.RemoveAsync(id)).ToHttp());

        d.MapPost("/{id:guid}/dismiss", async (Guid id, DiscoveryStore store) =>
            (await store.DismissAsync(id)).ToHttp());

        d.MapPost("/{id:guid}/restore", async (Guid id, DiscoveryStore store) =>
            (await store.RestoreAsync(id)).ToHttp());
    }

    /// <summary>A defined DiscoveryKind by name (case-insensitive); numbers are not accepted.</summary>
    private static bool TryParseKind(string? text, out ConCore.Discovery.DiscoveryKind kind)
    {
        kind = default;
        text = text?.Trim();
        return !string.IsNullOrEmpty(text) && char.IsLetter(text[0]) &&
            Enum.TryParse(text, ignoreCase: true, out kind) && Enum.IsDefined(kind);
    }

    private static IResult ToHttp(SearchOutcome outcome)
    {
        if (outcome.Busy)
            return ApiResults.Error(StatusCodes.Status409Conflict, "search-busy",
                "Another search is already running; try again in a moment.");
        if (outcome.TimedOut)
            return ApiResults.Error(StatusCodes.Status504GatewayTimeout, "search-timeout",
                "The search took too long and was stopped; nothing was found.");
        if (outcome.Errors.Count > 0)
            return ApiResults.Error(StatusCodes.Status400BadRequest, null, outcome.Errors.ToArray());
        return Results.Ok(new
        {
            found = outcome.Found, created = outcome.Created, updated = outcome.Updated,
            partial = outcome.Partial, warning = outcome.Warning,
        });
    }
}
