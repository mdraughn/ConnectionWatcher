using System.Text.Json.Serialization;
using ConPersistence.Config;
using ConWatch.Clone;

namespace ConWatch.Api;

/// <summary>The API's one error body, and the mappings from service outcomes onto HTTP.</summary>
public static class ApiResults
{
    /// <summary>{ "errors": [...] }, plus "code" when a client branches on the
    /// failure (the clone and take-over flows do).</summary>
    public sealed record ErrorBody(IReadOnlyList<string> Errors,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);

    public static IResult Error(int status, string? code, params string[] messages) =>
        Results.Json(new ErrorBody(messages, code), statusCode: status);

    public static IResult ToHttp(this ConfigResult result, Func<ConfigResult, IResult>? onSuccess = null)
    {
        if (result.Success)
            return onSuccess?.Invoke(result) ?? Results.NoContent();
        var errors = result.Errors?.ToArray() ?? [];
        return result.Kind switch
        {
            ConfigErrorKind.NotFound => Error(StatusCodes.Status404NotFound, null, errors),
            ConfigErrorKind.Conflict => Error(StatusCodes.Status409Conflict, null, errors),
            _ => Error(StatusCodes.Status400BadRequest, null, errors),
        };
    }

    /// <summary>Every failed CloneOutcome comes from CloneOutcome.Fail, which always sets a code.</summary>
    public static IResult ToHttp(this CloneOutcome outcome) =>
        outcome.Status == StatusCodes.Status200OK
            ? Results.Ok(outcome.Body)
            : Error(outcome.Status, outcome.ErrorCode, outcome.Error ?? "");
}
