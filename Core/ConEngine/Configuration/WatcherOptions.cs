using ConCore.Targets;

namespace ConEngine.Configuration;

/// <summary>Root configuration for the watcher: profiles, targets, pools.</summary>
public class WatcherOptions
{
    public const string SectionName = "Watcher";

    /// <summary>Named custom profiles; built-ins "Internal", "Path" and "Public" always exist.</summary>
    public Dictionary<string, PingProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TargetConfig> Targets { get; set; } = new();
    public List<PoolConfig> Pools { get; set; } = new();
}

/// <summary>One monitored endpoint, as authored in configuration.</summary>
public class TargetConfig
{
    /// <summary>Stable id; when null, the graph generates one (file-based config).</summary>
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>IP literal or DNS hostname.</summary>
    public string Address { get; set; } = "";
    /// <summary>Name of the parent target in the reachability tree.</summary>
    public string? Parent { get; set; }
    public string Profile { get; set; } = "Public";
    public bool Enabled { get; set; } = true;
    /// <summary>Optional operator/ISP name for attribution wording.</summary>
    public string? Operator { get; set; }
    /// <summary>Optional probe override ("tcp:&lt;port&gt;"); null means ICMP echo.</summary>
    public string? Probe { get; set; }
}

/// <summary>A rotation pool, as authored in configuration.</summary>
public class PoolConfig
{
    /// <summary>Stable id; when null, the graph generates one (file-based config).</summary>
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
    public string? Parent { get; set; }
}

/// <summary>Seconds-based configuration mirror of <see cref="PingProfile"/>.</summary>
public class PingProfileConfig
{
    public double NormalIntervalSeconds { get; set; } = 10;
    public double PingTimeoutSeconds { get; set; } = 3;
    public double BurstIntervalSeconds { get; set; } = 1;
    public double BurstMaxDurationSeconds { get; set; } = 60;
    public int HistorySize { get; set; } = 100;
    public int MinUsableSample { get; set; } = 10;
    public double RespondingThreshold { get; set; } = 0.95;
    public double FalteringThreshold { get; set; } = 0.75;
    public double DownThreshold { get; set; } = 0.10;
    public double RecoveringThreshold { get; set; } = 0.25;

    public PingProfile ToProfile(string name) => new()
    {
        Name = name,
        NormalInterval = TimeSpan.FromSeconds(NormalIntervalSeconds),
        PingTimeout = TimeSpan.FromSeconds(PingTimeoutSeconds),
        BurstInterval = TimeSpan.FromSeconds(BurstIntervalSeconds),
        BurstMaxDuration = TimeSpan.FromSeconds(BurstMaxDurationSeconds),
        HistorySize = HistorySize,
        MinUsableSample = MinUsableSample,
        RespondingThreshold = RespondingThreshold,
        FalteringThreshold = FalteringThreshold,
        DownThreshold = DownThreshold,
        RecoveringThreshold = RecoveringThreshold,
    };
}
