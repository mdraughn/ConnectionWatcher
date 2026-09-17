namespace ConPersistence.Entities;

/// <summary>Ping profile as stored; mirrors PingProfileConfig in seconds-based units.</summary>
public class ProfileRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
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
