using ConPersistence.Entities;

namespace ConPersistence.Dto;

/// <summary>
/// The complete sanitized state of a ConnectionWatcher instance, as served by
/// GET /api/v1/export/snapshot and consumed by <see cref="SnapshotImporter"/>.
/// Secret settings (see SettingKeys.SecretKeys) are omitted and listed in
/// RedactedSettings so the omission is visible, never silent.
/// </summary>
public record Snapshot
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset ExportedUtc { get; init; }
    public ConfigDocument Config { get; init; } = new();
    public Dictionary<string, string> Settings { get; init; } = new();
    public List<string> RedactedSettings { get; init; } = new();
    public List<TransitionRow> Transitions { get; init; } = new();
    public List<RollupRow> Rollups { get; init; } = new();
    public List<GlitchRow> Glitches { get; init; } = new();
}
