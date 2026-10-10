using System.Text.Json;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Snapshots;

namespace ConPersistence.Tests;

/// <summary>
/// The snapshot document is a wire format (clone, take-over, real-data
/// tests) serialized straight from the EF entity classes. A column change
/// changes it silently while SchemaVersion stays 1. When this test fails,
/// update the expected keys AND decide in the same change whether
/// Snapshot.SchemaVersion (and SnapshotImporter's accepted version) must move.
/// </summary>
public class SnapshotShapeTests
{
    private static void AssertKeys(JsonElement element, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(),
            element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

    [Fact]
    public void Snapshot_json_shape_is_pinned()
    {
        var now = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new Snapshot
        {
            ExportedUtc = new DateTimeOffset(now),
            Config = new ConfigDocument
            {
                Profiles = [new ProfileDto { Id = Guid.NewGuid(), Name = "P" }],
                Targets = [new TargetDto { Id = Guid.NewGuid(), Name = "T", Address = "10.0.0.1", Profile = "P" }],
                Pools = [new PoolDto { Id = Guid.NewGuid(), Name = "Pool", Members = ["T"] }],
            },
            Settings = new() { ["Verdict.AnchorPool"] = "Pool" },
            RedactedSettings = ["Mqtt.Password"],
            // Optional columns left null on purpose: a null must still be a key.
            Transitions = [new TransitionRow { Id = 1, TimestampUtc = now, SubjectName = "T" }],
            Rollups = [new RollupRow { Id = 1, PeriodStartUtc = now }],
            Glitches = [new GlitchRow { Id = 1, StartUtc = now, EndUtc = now, TargetName = "T" }],
        };

        var json = JsonSerializer.SerializeToElement(snapshot, JsonSerializerOptions.Web);

        AssertKeys(json, "schemaVersion", "exportedUtc", "config", "settings", "redactedSettings",
            "transitions", "rollups", "glitches");
        AssertKeys(json.GetProperty("config"), "profiles", "targets", "pools");
        AssertKeys(json.GetProperty("config").GetProperty("profiles")[0],
            "id", "name", "description", "normalIntervalSeconds", "pingTimeoutSeconds", "burstIntervalSeconds",
            "burstMaxDurationSeconds", "historySize", "minUsableSample", "respondingThreshold",
            "falteringThreshold", "downThreshold", "recoveringThreshold");
        AssertKeys(json.GetProperty("config").GetProperty("targets")[0],
            "id", "name", "address", "parent", "profile", "enabled", "operator", "probe", "scope", "critical");
        AssertKeys(json.GetProperty("config").GetProperty("pools")[0], "id", "name", "members", "parent");
        AssertKeys(json.GetProperty("transitions")[0],
            "id", "timestampUtc", "subjectId", "subjectName", "subjectKind", "fromState", "toState",
            "trigger", "successRate", "sampleSize", "burstLossRate", "ancestorId", "detail", "verdict", "avgRtt");
        AssertKeys(json.GetProperty("rollups")[0],
            "id", "targetId", "periodStartUtc", "resolution", "pingCount", "successCount", "rttAvg", "rttMin", "rttMax");
        AssertKeys(json.GetProperty("glitches")[0],
            "id", "targetId", "targetName", "class", "startUtc", "endUtc", "lostCount", "sampleCount", "detail");
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-09-25T00:00:00Z", json.GetProperty("transitions")[0].GetProperty("timestampUtc").GetString());
    }
}
