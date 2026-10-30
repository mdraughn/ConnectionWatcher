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
    public string Profile { get; set; } = PingProfile.Public.Name;
    public bool Enabled { get; set; } = true;
    /// <summary>Optional operator/ISP name for attribution wording.</summary>
    public string? Operator { get; set; }
    /// <summary>Optional probe override ("tcp:&lt;port&gt;"); null means ICMP echo.</summary>
    public string? Probe { get; set; }
    /// <summary>Verdict (default) or Local; Local targets never affect the verdict.</summary>
    public TargetScope Scope { get; set; } = TargetScope.Verdict;

    /// <summary>Notify when this is down, even if the internet is fine.</summary>
    public bool Critical { get; set; }

    /// <summary>Owned by the engine's system-resolver reconcile (spec 2026-10-10); never set by hand.</summary>
    public bool SystemManaged { get; set; }
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
    private static readonly PingProfile Default = new() { Name = "" };

    public double NormalIntervalSeconds { get; set; } = Default.NormalInterval.TotalSeconds;
    public double PingTimeoutSeconds { get; set; } = Default.PingTimeout.TotalSeconds;
    public double BurstIntervalSeconds { get; set; } = Default.BurstInterval.TotalSeconds;
    public double BurstMaxDurationSeconds { get; set; } = Default.BurstMaxDuration.TotalSeconds;
    public int HistorySize { get; set; } = Default.HistorySize;
    public int MinUsableSample { get; set; } = Default.MinUsableSample;
    public double RespondingThreshold { get; set; } = Default.RespondingThreshold;
    public double FalteringThreshold { get; set; } = Default.FalteringThreshold;
    public double DownThreshold { get; set; } = Default.DownThreshold;
    public double RecoveringThreshold { get; set; } = Default.RecoveringThreshold;

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
