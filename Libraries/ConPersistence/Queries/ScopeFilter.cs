namespace ConPersistence.Queries;

/// <summary>Which targets a history query covers: everything, the internet-facing
/// subjects (verdict-scope targets, pools, the verdict), or the LAN devices.</summary>
public enum ScopeFilter
{
    All,
    Internet,
    Local,
}

public static class ScopeFilters
{
    /// <summary>Null or empty means <see cref="ScopeFilter.All"/>; false for unknown text.</summary>
    public static bool TryParse(string? text, out ScopeFilter filter)
    {
        filter = ScopeFilter.All;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "all": return true;
            case "internet": filter = ScopeFilter.Internet; return true;
            case "local": filter = ScopeFilter.Local; return true;
            default: return false;
        }
    }
}
