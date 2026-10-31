using System.Text.Json;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Snapshots;

namespace ConWatch.Tests;

/// <summary>
/// Boots the full app against REAL production data when a snapshot is present
/// at testdata/nas-snapshot.json (repo root; gitignored — it describes the
/// home network). Skips cleanly when absent, so CI and fresh clones stay
/// green. The snapshot is imported into a temp database and pings are faked,
/// so these tests never touch the network or any production system.
///
/// To refresh: .\tools\Get-NasSnapshot.ps1 (fetches the open, secret-sanitized
/// /api/v1/export/snapshot endpoint).
/// </summary>
public class RealDataSnapshotTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private ConWatchFactory? _factory;
    private HttpClient? _client;
    private string? _tempDir;

    private static string? FindSnapshot()
    {
        // Walk up from the test bin dir to the repo root.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "testdata", "nas-snapshot.json");
            if (File.Exists(candidate))
                return candidate;
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return null;
        }
        return null;
    }

    public static bool SnapshotAvailable => FindSnapshot() is not null;

    public async Task InitializeAsync()
    {
        if (FindSnapshot() is not { } snapshotPath)
            return;
        var snapshot = JsonSerializer.Deserialize<Snapshot>(
            await File.ReadAllTextAsync(snapshotPath), Web)!;
        _tempDir = Directory.CreateTempSubdirectory("conwatch-snapshot").FullName;
        var dbPath = Path.Combine(_tempDir, "conwatch.db");
        await SnapshotImporter.ImportToFileAsync(dbPath, snapshot);
        _factory = new ConWatchFactory(dbPath);
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        try
        {
            if (_tempDir is not null)
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<JsonElement> GetJson(string url) =>
        JsonSerializer.Deserialize<JsonElement>(await _client!.GetStringAsync(url));

    [SkippableFact]
    public async Task App_boots_and_serves_the_real_topology()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.json");
        var status = await GetJson("/api/v1/status");
        Assert.True(status.GetProperty("targets").GetArrayLength() > 0,
            "snapshot should contain configured targets");
    }

    [SkippableFact]
    public async Task Real_transition_history_queries_cleanly()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.json");
        var transitions = await GetJson("/api/v1/transitions?limit=1000");
        Assert.True(transitions.GetProperty("items").GetArrayLength() > 0,
            "snapshot should contain recorded transitions");
    }

    [SkippableFact]
    public async Task Glitch_api_serves_real_rows_without_error()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.json");
        var glitches = await GetJson("/api/v1/glitches?limit=1000");
        // Count may legitimately be zero; the query itself must succeed.
        Assert.True(glitches.TryGetProperty("items", out _));
    }

    [SkippableFact]
    public async Task Isp_report_builds_from_real_data()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.json");
        var html = await _client!.GetStringAsync("/Report");
        Assert.Contains("Internet connection report", html);
        Assert.Contains("Brief disturbances", html);
    }

    [SkippableFact]
    public async Task History_and_dashboard_render_from_real_data()
    {
        Skip.IfNot(SnapshotAvailable, "no testdata/nas-snapshot.json");
        var history = await _client!.GetStringAsync("/History");
        Assert.Contains("history-root", history);
        var dashboard = await _client!.GetStringAsync("/");
        Assert.Contains("initial-data", dashboard);
    }
}
