namespace ConEngine.Configuration;

/// <summary>
/// Settings for the verdict layer. System-level tuning bound from the
/// appsettings "Verdict" section; not part of the CRUD'd monitoring config.
/// </summary>
public class VerdictOptions
{
    public const string SectionName = "Verdict";

    /// <summary>Name of the pool whose members are the internet anchors. Null disables the verdict layer.</summary>
    public string? AnchorPool { get; set; }

    /// <summary>Degraded-anchor count at which SiteIncident becomes InternetWeather.</summary>
    public int WeatherThreshold { get; set; } = 2;
}
