using ConCore.Targets;
using ConEngine.Configuration;

namespace ConPersistence.Config;

/// <summary>Ping profile for API/CRUD and export. Name-keyed; Id preserved when present.
/// A field the caller leaves out takes <see cref="PingProfileConfig"/>'s default,
/// the one place those numbers are written down.</summary>
public record ProfileDto
{
    private static readonly PingProfileConfig Defaults = new();

    public Guid? Id { get; init; }
    public required string Name { get; init; }
    /// <summary>What this profile is for; trimmed, at most 500 characters, blank stored as null.</summary>
    public string? Description { get; init; }
    public double NormalIntervalSeconds { get; init; } = Defaults.NormalIntervalSeconds;
    public double PingTimeoutSeconds { get; init; } = Defaults.PingTimeoutSeconds;
    public double BurstIntervalSeconds { get; init; } = Defaults.BurstIntervalSeconds;
    public double BurstMaxDurationSeconds { get; init; } = Defaults.BurstMaxDurationSeconds;
    public int HistorySize { get; init; } = Defaults.HistorySize;
    public int MinUsableSample { get; init; } = Defaults.MinUsableSample;
    public double RespondingThreshold { get; init; } = Defaults.RespondingThreshold;
    public double FalteringThreshold { get; init; } = Defaults.FalteringThreshold;
    public double DownThreshold { get; init; } = Defaults.DownThreshold;
    public double RecoveringThreshold { get; init; } = Defaults.RecoveringThreshold;
}

/// <summary>Target for API/CRUD and export. Parent/Profile referenced by name.</summary>
public record TargetDto
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public string? Parent { get; init; }
    public string Profile { get; init; } = "Public";
    public bool Enabled { get; init; } = true;
    /// <summary>Optional operator/ISP name ("Comcast") for attribution wording.</summary>
    public string? Operator { get; init; }
    /// <summary>Optional probe override ("tcp:&lt;port&gt;"); null means ICMP echo.</summary>
    public string? Probe { get; init; }
    /// <summary>Verdict (default) or Local; a missing value (older clients/snapshots) means Verdict.</summary>
    public TargetScope Scope { get; init; } = TargetScope.Verdict;

    /// <summary>Notify when this is down, even if the internet is fine. A
    /// missing value (older clients/snapshots) means false.</summary>
    public bool Critical { get; init; }

    /// <summary>Created and retired by ConnectionWatcher from the host's OS resolver
    /// list. Read-only through the targets API: the flag is ignored on create and
    /// preserved on update (a managed row stays managed, a plain row stays plain). A missing value (older clients/snapshots) means false.</summary>
    public bool SystemManaged { get; init; }
}

/// <summary>Pool for API/CRUD and export. Members/Parent referenced by name.</summary>
public record PoolDto
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public List<string> Members { get; init; } = new();
    public string? Parent { get; init; }
}

/// <summary>Whole-config document for export/import.</summary>
public record ConfigDocument
{
    public List<ProfileDto> Profiles { get; init; } = new();
    public List<TargetDto> Targets { get; init; } = new();
    public List<PoolDto> Pools { get; init; } = new();
}
