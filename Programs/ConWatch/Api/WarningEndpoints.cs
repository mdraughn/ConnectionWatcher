using ConPersistence.Warnings;

namespace ConWatch.Api;

/// <summary>Admin notes (model warnings). Reading is part of /status; only
/// acknowledging lives here, and it is admin-only.</summary>
public static class WarningEndpoints
{
    public static void MapWarnings(this RouteGroupBuilder api)
    {
        // Hides that instance until it clears and recurs (spec §9.4); the
        // check itself stays on.
        api.MapPost("/warnings/{key}/acknowledge", (string key, ModelWarningLedger warnings) =>
            warnings.Acknowledge(key)
                ? Results.NoContent()
                : ApiResults.Error(StatusCodes.Status404NotFound, null, $"No active note '{key}'."));
    }
}
