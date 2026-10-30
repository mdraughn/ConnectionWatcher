namespace ConCore.Targets;

/// <summary>The one rule for picking a target name that no other target already has.</summary>
public static class TargetNames
{
    /// <summary>The longest a target name may be.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// <paramref name="wanted"/> trimmed, when no name in <paramref name="taken"/> equals it
    /// (case-insensitively); otherwise the first free "{name} {n}" for n = 2, 3, and so on.
    /// The result never exceeds <see cref="MaxLength"/>: the base is cut to make room for the suffix.
    /// </summary>
    public static string Unique(string wanted, IEnumerable<string> taken)
    {
        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var name = wanted.Trim();
        if (name.Length > MaxLength)
            name = name[..MaxLength].TrimEnd();
        if (!names.Contains(name))
            return name;
        for (var n = 2; ; n++)
        {
            var suffix = $" {n}";
            var candidate = (name.Length + suffix.Length > MaxLength ? name[..(MaxLength - suffix.Length)].TrimEnd() : name) + suffix;
            if (!names.Contains(candidate))
                return candidate;
        }
    }
}
