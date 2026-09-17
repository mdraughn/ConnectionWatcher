using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConPersistence.Entities;

namespace ConPersistence.Dto;

/// <summary>
/// The one place that knows how profiles, targets and pools map between
/// their four shapes: engine options (<see cref="PingProfileConfig"/>,
/// <see cref="TargetConfig"/>, <see cref="PoolConfig"/>), API/export DTOs,
/// stored rows, and the engine's <see cref="PingProfile"/> presets. Adding a
/// field means touching this file, not six call sites.
/// </summary>
internal static class ConfigMapping
{
    // ---- normalization shared by every write path ----

    /// <summary>
    /// Canonicalizes a probe string so downstream parsers (e.g. the form's
    /// strict "tcp:&lt;port&gt;" regex) never misread a differently-cased or
    /// padded value ("TCP:80", " tcp:80 ", "icmp"). Unparseable strings are
    /// stored unchanged so the validator, not this helper, owns rejection.
    /// </summary>
    public static string? NormalizeProbe(string? probe) =>
        ProbeSpec.TryParse(probe, out var spec) ? spec.ToConfigString() : probe;

    /// <summary>Blank operator names are stored as null.</summary>
    public static string? NormalizeOperator(string? op) =>
        op?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    // ---- profiles ----

    public static ProfileDto ToDto(this ProfileRow p) => new()
    {
        Id = p.Id, Name = p.Name,
        NormalIntervalSeconds = p.NormalIntervalSeconds,
        PingTimeoutSeconds = p.PingTimeoutSeconds,
        BurstIntervalSeconds = p.BurstIntervalSeconds,
        BurstMaxDurationSeconds = p.BurstMaxDurationSeconds,
        HistorySize = p.HistorySize,
        MinUsableSample = p.MinUsableSample,
        RespondingThreshold = p.RespondingThreshold,
        FalteringThreshold = p.FalteringThreshold,
        DownThreshold = p.DownThreshold,
        RecoveringThreshold = p.RecoveringThreshold,
    };

    public static ProfileDto ToDto(this PingProfile p, Guid? id = null) => new()
    {
        Id = id, Name = p.Name,
        NormalIntervalSeconds = p.NormalInterval.TotalSeconds,
        PingTimeoutSeconds = p.PingTimeout.TotalSeconds,
        BurstIntervalSeconds = p.BurstInterval.TotalSeconds,
        BurstMaxDurationSeconds = p.BurstMaxDuration.TotalSeconds,
        HistorySize = p.HistorySize,
        MinUsableSample = p.MinUsableSample,
        RespondingThreshold = p.RespondingThreshold,
        FalteringThreshold = p.FalteringThreshold,
        DownThreshold = p.DownThreshold,
        RecoveringThreshold = p.RecoveringThreshold,
    };

    public static PingProfileConfig ToConfig(this ProfileRow p) => new()
    {
        NormalIntervalSeconds = p.NormalIntervalSeconds,
        PingTimeoutSeconds = p.PingTimeoutSeconds,
        BurstIntervalSeconds = p.BurstIntervalSeconds,
        BurstMaxDurationSeconds = p.BurstMaxDurationSeconds,
        HistorySize = p.HistorySize,
        MinUsableSample = p.MinUsableSample,
        RespondingThreshold = p.RespondingThreshold,
        FalteringThreshold = p.FalteringThreshold,
        DownThreshold = p.DownThreshold,
        RecoveringThreshold = p.RecoveringThreshold,
    };

    public static PingProfileConfig ToConfig(this ProfileDto p) => new()
    {
        NormalIntervalSeconds = p.NormalIntervalSeconds,
        PingTimeoutSeconds = p.PingTimeoutSeconds,
        BurstIntervalSeconds = p.BurstIntervalSeconds,
        BurstMaxDurationSeconds = p.BurstMaxDurationSeconds,
        HistorySize = p.HistorySize,
        MinUsableSample = p.MinUsableSample,
        RespondingThreshold = p.RespondingThreshold,
        FalteringThreshold = p.FalteringThreshold,
        DownThreshold = p.DownThreshold,
        RecoveringThreshold = p.RecoveringThreshold,
    };

    /// <summary>Copies every editable field (not the id) from a DTO onto a row.</summary>
    public static ProfileRow CopyFrom(this ProfileRow row, ProfileDto dto)
    {
        row.Name = dto.Name;
        row.NormalIntervalSeconds = dto.NormalIntervalSeconds;
        row.PingTimeoutSeconds = dto.PingTimeoutSeconds;
        row.BurstIntervalSeconds = dto.BurstIntervalSeconds;
        row.BurstMaxDurationSeconds = dto.BurstMaxDurationSeconds;
        row.HistorySize = dto.HistorySize;
        row.MinUsableSample = dto.MinUsableSample;
        row.RespondingThreshold = dto.RespondingThreshold;
        row.FalteringThreshold = dto.FalteringThreshold;
        row.DownThreshold = dto.DownThreshold;
        row.RecoveringThreshold = dto.RecoveringThreshold;
        return row;
    }

    public static ProfileRow ToRow(this ProfileDto dto, Guid id) => new ProfileRow { Id = id }.CopyFrom(dto);

    // ---- targets ----

    public static TargetDto ToDto(this TargetConfig t) => new()
    {
        Id = t.Id, Name = t.Name, Address = t.Address,
        Parent = t.Parent, Profile = t.Profile, Enabled = t.Enabled,
        Operator = t.Operator, Probe = t.Probe,
    };

    /// <summary>Engine-options shape with the probe canonicalized (the validator sees what will be stored).</summary>
    public static TargetConfig ToConfig(this TargetDto t) => new()
    {
        Id = t.Id, Name = t.Name, Address = t.Address,
        Parent = t.Parent, Profile = t.Profile, Enabled = t.Enabled,
        Operator = t.Operator, Probe = NormalizeProbe(t.Probe),
    };

    public static TargetConfig ToConfig(this TargetRow t, string? parentName, string profileName) => new()
    {
        Id = t.Id, Name = t.Name, Address = t.Address,
        Parent = parentName, Profile = profileName, Enabled = t.Enabled,
        Operator = t.Operator, Probe = t.Probe,
    };

    // ---- pools ----

    public static PoolDto ToDto(this PoolConfig p) => new()
    {
        Id = p.Id, Name = p.Name, Members = p.Members, Parent = p.Parent,
    };

    public static PoolConfig ToConfig(this PoolDto p) => new()
    {
        Id = p.Id, Name = p.Name, Members = p.Members, Parent = p.Parent,
    };

    public static PoolConfig ToConfig(this PoolRow p, string? parentName, List<string> memberNames) => new()
    {
        Id = p.Id, Name = p.Name, Parent = parentName, Members = memberNames,
    };

    // ---- whole documents ----

    /// <summary>A document in engine-options shape, ready for the validator.</summary>
    public static WatcherOptions ToOptions(this ConfigDocument doc)
    {
        var options = new WatcherOptions();
        foreach (var p in doc.Profiles)
            options.Profiles[p.Name] = p.ToConfig();
        options.Targets.AddRange(doc.Targets.Select(t => t.ToConfig()));
        options.Pools.AddRange(doc.Pools.Select(p => p.ToConfig()));
        return options;
    }
}
