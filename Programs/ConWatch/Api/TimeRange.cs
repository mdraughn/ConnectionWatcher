using System.Globalization;

namespace ConWatch.Api;

/// <summary>
/// One rule for timestamps that arrive as text or as bare DateTimes: ISO-8601,
/// an explicit offset is honored, and NO offset means UTC. (ASP.NET's
/// DateTime binder would treat offset-less text as server-local time, which
/// shifted every curl/Grafana/MCP query by the host's zone.)
/// </summary>
public static class TimeRange
{
    /// <summary>Parses query text to a UTC DateTime; null/blank passes through.</summary>
    /// <exception cref="ArgumentException">Not a recognizable timestamp.</exception>
    public static DateTime? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new ArgumentException($"'{text}' is not a valid timestamp");
        return parsed.UtcDateTime;
    }

    /// <summary>A DateTime from a JSON body or tool argument: Unspecified kind is UTC, not local.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// Parses an optional from/to pair. On failure <paramref name="problem"/>
    /// is a 400 naming the offending parameter, ready to return.
    /// </summary>
    public static bool TryParseRange(string? from, string? to,
        out DateTime? fromUtc, out DateTime? toUtc, out IResult? problem)
    {
        fromUtc = null;
        toUtc = null;
        problem = null;
        try
        {
            fromUtc = ParseUtc(from);
        }
        catch (ArgumentException)
        {
            problem = Results.BadRequest(new { errors = new[] { $"'from' is not a valid timestamp: {from}" } });
            return false;
        }
        try
        {
            toUtc = ParseUtc(to);
        }
        catch (ArgumentException)
        {
            problem = Results.BadRequest(new { errors = new[] { $"'to' is not a valid timestamp: {to}" } });
            return false;
        }
        return true;
    }
}
