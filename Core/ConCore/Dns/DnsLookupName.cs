using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConCore.Dns;

/// <summary>
/// A name the resolver watch asks for (spec 2026-10-10 §4.2). A seeded name
/// carries the addresses it is known to resolve to, so a wrong answer is a
/// miss; an admin-added name has none, so any real answer is a hit.
/// </summary>
public sealed record DnsLookupName(string Name, IReadOnlyList<string> Expected)
{
    public bool HasExpected => Expected.Count > 0;

    /// <summary>The four anchor operators' own names and published addresses (spec R3).</summary>
    public static IReadOnlyList<DnsLookupName> Seeded { get; } =
    [
        new("dns.google", ["8.8.8.8", "8.8.4.4"]),
        new("one.one.one.one", ["1.1.1.1", "1.0.0.1"]),
        new("dns.quad9.net", ["9.9.9.9", "149.112.112.112"]),
        new("dns.opendns.com", ["208.67.222.222", "208.67.220.220"]),
    ];

    private sealed record Dto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("expected")] List<string>? Expected);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The settings-row form: a JSON array of {name, expected[]}.</summary>
    public static string ToJson(IEnumerable<DnsLookupName> names) =>
        JsonSerializer.Serialize(names.Select(n => new Dto(n.Name, n.Expected.Count > 0 ? n.Expected.ToList() : null)), Json);

    /// <summary>Parses the settings-row form; null, blank or invalid JSON reads as no names.</summary>
    public static IReadOnlyList<DnsLookupName> FromJson(string? json) =>
        TryFromJson(json, out var names) ? names : [];

    /// <summary>False only when the text is not a JSON array of objects. Null or
    /// blank text is valid and means no names. Null elements, blank names and
    /// expected entries that are not IP literals are dropped, not errors.</summary>
    public static bool TryFromJson(string? json, out IReadOnlyList<DnsLookupName> names)
    {
        names = [];
        if (string.IsNullOrWhiteSpace(json))
            return true;
        List<Dto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<Dto>>(json, Json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (dtos is null)
            return false;
        names = dtos
            .Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => new DnsLookupName(
                d.Name!.Trim(),
                (d.Expected ?? []).Where(e => IPAddress.TryParse(e, out _)).Select(e => e.Trim()).ToList()))
            .ToList();
        return true;
    }

    public bool Equals(DnsLookupName? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) &&
        Expected.SequenceEqual(other.Expected);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}
