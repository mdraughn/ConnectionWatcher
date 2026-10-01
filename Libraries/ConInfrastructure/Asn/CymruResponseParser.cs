using System.Net;
using System.Text.RegularExpressions;

namespace ConInfrastructure.Asn;

/// <summary>
/// Pure parsing for Team Cymru's IP-to-ASN DNS service
/// (https://www.team-cymru.com/ip-asn-mapping). Two TXT lookups:
///   4.3.2.1.origin.asn.cymru.com → "7922 | 73.0.0.0/8 | US | arin | 2005-01-27"
///   AS7922.asn.cymru.com         → "7922 | US | arin | 2005-01-27 | COMCAST-7922 - Comcast Cable Communications, LLC, US"
/// The name is reduced to the registry handle (text before the first " - ").
/// </summary>
public static partial class CymruResponseParser
{
    public static string OriginQueryName(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new ArgumentException("Only IPv4 addresses can be looked up.", nameof(address));
        var b = address.GetAddressBytes();
        return $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.origin.asn.cymru.com";
    }

    public static string AsQueryName(int asn) => $"AS{asn}.asn.cymru.com";

    /// <summary>The first ASN of the origin answer; null when there is none.</summary>
    public static int? ParseOrigin(string? text)
    {
        var first = Fields(text).FirstOrDefault();
        if (first is null)
            return null;
        var token = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(token, out var asn) && asn > 0 ? asn : null;
    }

    /// <summary>The AS handle (trailing ", CC" country and " - description" removed), plus that country.</summary>
    public static (string Name, string? CountryCode)? ParseAsName(string? text)
    {
        var fields = Fields(text);
        if (fields.Count < 5 || fields[4].Length == 0)
            return null;
        var match = CountrySuffix().Match(fields[4]);
        var name = match.Success ? fields[4][..match.Index].TrimEnd() : fields[4];
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0)
            name = name[..dash].Trim();
        if (name.Length == 0)
            return null;
        return (name, match.Success ? match.Groups[1].Value : null);
    }

    private static List<string> Fields(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split('|').Select(f => f.Trim()).ToList();

    [GeneratedRegex(@",\s*([A-Z]{2})$")]
    private static partial Regex CountrySuffix();
}
