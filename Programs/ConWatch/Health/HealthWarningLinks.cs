using ConPersistence.Config;

namespace ConWatch.Health;

/// <summary>Where a configuration warning is fixed in the UI. A host concern: the warning
/// itself (code and message) is persistence-level, the pages are not.</summary>
public static class HealthWarningLinks
{
    /// <summary>The page where this warning is fixed, so a banner can send the person
    /// there rather than to a tab that cannot act on it.</summary>
    public static string Page(this HealthWarning w) => w.Code switch
    {
        "dns-not-watched" or "no-upstream-resolver" or "dns-over-https" => "/Config/Dns",
        "no-anchor-pool" or "anchor-pool-missing" or "anchors-below-threshold"
            or "anchors-below-recommended" or "pool-parent-drift" => "/Config/Pools",
        "clone-source-active" => "/Settings",
        _ => "/Config/Targets", // anchors-all-isp (operator tags), tcp-refused (probe), anything new
    };

    /// <summary>True when the fix lives on a Config tab (as opposed to the Settings page).</summary>
    public static bool IsConfigTab(this HealthWarning w) => w.Page().StartsWith("/Config/", StringComparison.Ordinal);

    /// <summary>The page's name as the UI labels it ("DNS", "Pools", "Targets", "Settings").</summary>
    public static string PageName(this HealthWarning w)
    {
        var page = w.Page();
        return page[(page.LastIndexOf('/') + 1)..] switch
        {
            "Dns" => "DNS",
            var name => name,
        };
    }
}
