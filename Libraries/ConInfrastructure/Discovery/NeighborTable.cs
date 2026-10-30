using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace ConInfrastructure.Discovery;

/// <summary>An IP the machine has recently talked to on its LAN, with its MAC (lowercase, colon-separated).</summary>
public sealed record NeighborEntry(string Address, string Mac);

public interface INeighborTable
{
    IReadOnlyList<NeighborEntry> Read();
}

/// <summary>Pure parsers for the OS neighbor tables. They never throw: unparseable lines are skipped.</summary>
public static partial class NeighborParser
{
    [GeneratedRegex(@"\b(\d{1,3}(?:\.\d{1,3}){3})\b")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:\-])([0-9A-Fa-f]{1,2})([:\-])([0-9A-Fa-f]{1,2})(?:\2[0-9A-Fa-f]{1,2}){4}(?![0-9A-Fa-f:\-])")]
    private static partial Regex MacAddress();

    /// <summary>Linux <c>/proc/net/arp</c>: header, then <c>IP  HWtype  Flags  HWaddr  Mask  Device</c>; flags 0x0 is an incomplete entry.</summary>
    public static IReadOnlyList<NeighborEntry> ParseProcNetArp(string? text)
    {
        var entries = new List<NeighborEntry>();
        foreach (var line in (text ?? "").Split('\n').Skip(1))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[2] == "0x0")
                continue;
            if (IsIpv4(parts[0]) && Net.MacAddress.TryNormalize(parts[3], out var mac))
                entries.Add(new NeighborEntry(parts[0], mac));
        }
        return entries;
    }

    /// <summary>Windows <c>arp -a</c> and macOS <c>arp -an</c>: an IPv4 and a MAC on the same line.</summary>
    public static IReadOnlyList<NeighborEntry> ParseArpCommand(string? text)
    {
        var entries = new List<NeighborEntry>();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var ip = Ipv4().Match(line);
            var macMatch = MacAddress().Match(line);
            if (!ip.Success || !macMatch.Success || !IsIpv4(ip.Groups[1].Value))
                continue;
            if (Net.MacAddress.TryNormalize(macMatch.Value, out var mac))
                entries.Add(new NeighborEntry(ip.Groups[1].Value, mac));
        }
        return entries;
    }

    private static bool IsIpv4(string text) =>
        IPAddress.TryParse(text, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && text.Count(c => c == '.') == 3;
}

/// <summary>The machine's own neighbor table. Any failure — no <c>arp</c>, no permission, a hang — is an empty list:
/// the ping sweep's results still stand without it.</summary>
public sealed class SystemNeighborTable : INeighborTable
{
    public IReadOnlyList<NeighborEntry> Read()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/net/arp"))
                return NeighborParser.ParseProcNetArp(File.ReadAllText("/proc/net/arp"));

            var start = new ProcessStartInfo("arp", OperatingSystem.IsWindows() ? "-a" : "-an")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                return [];
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return [];
            }
            return NeighborParser.ParseArpCommand(output.GetAwaiter().GetResult());
        }
        catch (Exception)
        {
            return [];
        }
    }
}
