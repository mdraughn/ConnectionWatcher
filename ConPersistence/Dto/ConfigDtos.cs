namespace ConPersistence.Dto;

/// <summary>Ping profile for API/CRUD and export. Name-keyed; Id preserved when present.</summary>
public record ProfileDto
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public double NormalIntervalSeconds { get; init; } = 10;
    public double PingTimeoutSeconds { get; init; } = 3;
    public double BurstIntervalSeconds { get; init; } = 1;
    public double BurstMaxDurationSeconds { get; init; } = 60;
    public int HistorySize { get; init; } = 100;
    public int MinUsableSample { get; init; } = 10;
    public double RespondingThreshold { get; init; } = 0.95;
    public double FalteringThreshold { get; init; } = 0.75;
    public double DownThreshold { get; init; } = 0.10;
    public double RecoveringThreshold { get; init; } = 0.25;
}

/// <summary>Target for API/CRUD and export. Parent/Profile referenced by name.</summary>
public record TargetDto
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public string? Parent { get; init; }
    public string Profile { get; init; } = "External";
    public bool Enabled { get; init; } = true;
    /// <summary>Optional operator/ISP name ("Comcast") for attribution wording.</summary>
    public string? Operator { get; init; }
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
