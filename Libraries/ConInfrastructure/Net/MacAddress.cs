namespace ConInfrastructure.Net;

/// <summary>MAC address text handling.</summary>
public static class MacAddress
{
    /// <summary>Normalizes "AA-BB-..", "a:b:c:d:e:f" (macOS drops leading zeros) to "aa:bb:..";
    /// false for malformed, all-zero, broadcast and multicast MACs.</summary>
    public static bool TryNormalize(string text, out string mac)
    {
        mac = "";
        var octets = text.Split(':', '-');
        if (octets.Length != 6)
            return false;
        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (octets[i].Length is < 1 or > 2 || !byte.TryParse(octets[i], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        }
        if (bytes.All(b => b == 0) || bytes.All(b => b == 0xff) || (bytes[0] & 1) == 1)   // zero, broadcast, multicast
            return false;
        mac = string.Join(':', bytes.Select(b => b.ToString("x2")));
        return true;
    }
}
