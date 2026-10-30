using System.Text.Json;

namespace ConWatch;

/// <summary>
/// The host's one JSON options instance (<see cref="JsonSerializerDefaults.Web"/>). It is shared by every
/// caller in this assembly and must never be mutated.
/// </summary>
public static class WebJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
