using ConPersistence;

namespace ConWatch.Api;

/// <summary>Maps ConfigResult outcomes onto HTTP responses.</summary>
public static class ApiResults
{
    public static IResult ToHttp(this ConfigResult result, Func<ConfigResult, IResult>? onSuccess = null)
    {
        if (result.Success)
            return onSuccess?.Invoke(result) ?? Results.NoContent();
        return result.Kind switch
        {
            ConfigErrorKind.NotFound => Results.NotFound(new { errors = result.Errors }),
            ConfigErrorKind.Conflict => Results.Conflict(new { errors = result.Errors }),
            _ => Results.BadRequest(new { errors = result.Errors }),
        };
    }
}
