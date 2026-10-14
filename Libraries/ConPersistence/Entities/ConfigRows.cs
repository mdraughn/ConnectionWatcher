using ConCore.Targets;
using ConEngine.Configuration;

namespace ConPersistence.Entities;

/// <summary>Ping profile as stored; mirrors PingProfileConfig in seconds-based units
/// and starts from its defaults.</summary>
public class ProfileRow
{
    private static readonly PingProfileConfig Defaults = new();

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>What this profile is for, shown beside its name. Null when the user left it blank.</summary>
    public string? Description { get; set; }
    public double NormalIntervalSeconds { get; set; } = Defaults.NormalIntervalSeconds;
    public double PingTimeoutSeconds { get; set; } = Defaults.PingTimeoutSeconds;
    public double BurstIntervalSeconds { get; set; } = Defaults.BurstIntervalSeconds;
    public double BurstMaxDurationSeconds { get; set; } = Defaults.BurstMaxDurationSeconds;
    public int HistorySize { get; set; } = Defaults.HistorySize;
    public int MinUsableSample { get; set; } = Defaults.MinUsableSample;
    public double RespondingThreshold { get; set; } = Defaults.RespondingThreshold;
    public double FalteringThreshold { get; set; } = Defaults.FalteringThreshold;
    public double DownThreshold { get; set; } = Defaults.DownThreshold;
    public double RecoveringThreshold { get; set; } = Defaults.RecoveringThreshold;
}

/// <summary>A monitored endpoint as stored.</summary>
public class TargetRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public Guid? ParentId { get; set; }
    public Guid ProfileId { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Optional operator/ISP name for attribution wording.</summary>
    public string? Operator { get; set; }
    /// <summary>Optional probe override ("tcp:&lt;port&gt;"); null means ICMP echo.</summary>
    public string? Probe { get; set; }
    /// <summary>Verdict (default) or Local; stored as text.</summary>
    public TargetScope Scope { get; set; } = TargetScope.Verdict;

    public bool Critical { get; set; }

    public bool SystemManaged { get; set; }
}

/// <summary>A rotation pool as stored.</summary>
public class PoolRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public List<PoolMemberRow> Members { get; set; } = new();
}

/// <summary>Pool membership; MemberIndex drives phase stagger order.</summary>
public class PoolMemberRow
{
    public Guid PoolId { get; set; }
    public Guid TargetId { get; set; }
    public int MemberIndex { get; set; }
}
