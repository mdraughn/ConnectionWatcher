using System.Text.Json;

namespace ConWatch.Tests;

/// <summary>
/// Boots the full app against a snapshot of REAL production data when one is
/// present at testdata/nas-snapshot.db (repo root; gitignored — it contains
/// the MQTT password in Settings). Skips cleanly when absent, so CI and
/// fresh clones stay green. The snapshot is copied to a temp dir first and
/// pings are faked, so these tests never mutate the snapshot or touch the
/// network.
///
/// To refresh the snapshot: stop the conwatch project on the NAS, download
/// docker/conwatch/data/conwatch.db via File Station into testdata/ as
/// nas-snapshot.db, restart the project.
/// </summary>
public class RealDataSnapshotTests : IDisposable
{
    private readonly ConWatchFactory? _factory;
    private readonly HttpClient? _client;
    private readonly string? _tempDir;

    private static string? FindSnapshot()
    {
        // Walk up from the test bin dir to the repo root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "testdata", "nas-snapshot.db");
            if (File.Exists(candidate))
                return candidate;
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return null;
        }
        return null;
    }

    public static bool SnapshotAvailable => FindSnapshot() is not null;

    public RealDataSnapshotTests()
    {
        if (FindSnapshot() is not { } snapshot)
            return;
        // Copy: the app writes (rollups, transitions) as soon as it boots,
        // and the pristine snapshot must survive the run.
        _tempDir = Directory.CreateTempSubdirectory("conwatch-snapshot").FullName;
        var dbPath = Path.Combine(_tempDir, "conwatch.db");
        File.Copy(snapshot, dbPath);
        _factory = new ConWatchFactory(dbPath);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        try
        {
            if (_tempDir is not null)
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private async Task<JsonElement> GetJson(string url) =>
        JsonSerializer.Deserialize<JsonElement>(await _client!.GetStringAsync(url));

    [SkippableFact]
    public async Task App_boots_and_serves_the_real_topology()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.db");
        var status = await GetJson("/api/v1/status");
        Assert.True(status.GetProperty("targets").GetArrayLength() > 0,
            "snapshot should contain configured targets");
    }

    [SkippableFact]
    public async Task Real_transition_history_queries_cleanly()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.db");
        var transitions = await GetJson("/api/v1/transitions?limit=1000");
        Assert.True(transitions.GetProperty("items").GetArrayLength() > 0,
            "snapshot should contain recorded transitions");
    }

    [SkippableFact]
    public async Task Glitch_api_serves_real_rows_without_error()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.db");
        var glitches = await GetJson("/api/v1/glitches?limit=1000");
        // Count may legitimately be zero; the query itself must succeed.
        Assert.True(glitches.TryGetProperty("items", out _));
    }

    [SkippableFact]
    public async Task Isp_report_builds_from_real_data()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.db");
        var html = await _client!.GetStringAsync("/Report");
        Assert.Contains("Internet connection report", html);
        Assert.Contains("Brief disturbances", html);
    }

    [SkippableFact]
    public async Task History_and_dashboard_render_from_real_data()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.db");
        var history = await _client!.GetStringAsync("/History");
        Assert.Contains("history-root", history);
        var dashboard = await _client!.GetStringAsync("/");
        Assert.Contains("initial-data", dashboard);
    }
}
