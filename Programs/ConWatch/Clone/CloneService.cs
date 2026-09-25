using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence;
using ConPersistence.Settings;
using ConPersistence.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace ConWatch.Clone;

/// <summary>Outcome of a clone or take-over step: HTTP-mappable without the
/// service knowing about HTTP results.</summary>
public sealed record CloneOutcome(int Status, string? ErrorCode = null, string? Error = null,
    object? Body = null)
{
    public static CloneOutcome Ok(object body) => new(200, Body: body);
    public static CloneOutcome Fail(int status, string code, string message) =>
        new(status, code, message);
}

/// <summary>
/// Cross-node choreography: clone this node from a running source, and later
/// take over from it (disable source, final full sync, enable here). All
/// outbound HTTP goes through IHttpClientFactory("clone") so tests can route
/// it at a second in-memory app.
/// </summary>
public class CloneService(IHttpClientFactory httpFactory,
    IDbContextFactory<ConWatchDbContext> dbFactory, SettingsStore settings,
    WatcherHostService host, ILogger<CloneService> log)
{
    public const string HttpClientName = "clone";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CloneOutcome> CloneAsync(string? sourceUrl, string? sourcePassword,
        bool includeSecrets, bool allowInsecure, CancellationToken ct)
    {
        if (!TryParseSource(sourceUrl, out var source, out var parseError))
            return parseError!;

        var sensitive = includeSecrets || sourcePassword is { Length: > 0 };
        if (TransportPolicy.Evaluate(source, sensitive, allowInsecure) ==
            TransportPolicy.Verdict.RequiresOverride)
        {
            return CloneOutcome.Fail(400, "insecure-transport",
                "This clone would send the source admin password and receive its secrets " +
                "over unencrypted HTTP. Use the source's HTTPS address, confirm the " +
                "insecure transfer, or clone without secrets.");
        }

        var fetched = await FetchSnapshotAsync(source, sourcePassword, includeSecrets, ct);
        if (fetched.Outcome is { } fetchError)
            return fetchError;
        var snapshot = fetched.Snapshot!;

        // Arrive as a disabled archive: the source keeps monitoring; nothing
        // double-pings and MQTT entities don't fight. Take-over flips this.
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.EngineEnabled] = "false",
            [SettingKeys.CloneSourceUrl] = source.ToString(),
        });

        try
        {
            await SnapshotImporter.ImportAsync(dbFactory, snapshot, ct);
        }
        catch (InvalidOperationException ex)
        {
            return CloneOutcome.Fail(400, "import-failed", ex.Message);
        }

        if (!await host.RebuildAsync()) // deterministic parked-archive state before answering
            return CloneOutcome.Fail(500, "rebuild-failed",
                "The clone was imported but the archive could not be prepared; see this node's log.");
        log.LogInformation("Cloned from {Source}: {Targets} targets, {Transitions} transitions",
            source, snapshot.Config.Targets.Count, snapshot.Transitions.Count);
        return CloneOutcome.Ok(Summary(snapshot, includeSecrets));
    }

    public async Task<CloneOutcome> TakeOverAsync(string? sourcePassword, bool allowInsecure, CancellationToken ct)
    {
        var stored = await settings.GetAsync(SettingKeys.CloneSourceUrl);
        if (!TryParseSource(stored, out var source, out _))
            return CloneOutcome.Fail(400, "no-clone-source",
                "This node was not cloned from another — there is nothing to take over from.");

        // Take-over always carries the password and always pulls secrets. The
        // consent given at clone time may have covered a non-sensitive
        // transfer (no password, no secrets), so it is asked for again here.
        if (TransportPolicy.Evaluate(source, sensitive: true, allowInsecure) ==
            TransportPolicy.Verdict.RequiresOverride)
        {
            return CloneOutcome.Fail(400, "insecure-transport",
                "Taking over would send the source admin password and receive its secrets " +
                "over unencrypted HTTP. Use the source's HTTPS address or confirm the " +
                "insecure transfer.");
        }

        var client = httpFactory.CreateClient(HttpClientName);

        // 1. Disable the source. Its engine stops and flushes before the call
        //    returns, so the final snapshot below is complete.
        HttpResponseMessage disable;
        try
        {
            disable = await client.PostAsJsonAsync(new Uri(source, "api/v1/admin/engine"),
                new { enabled = false, password = sourcePassword }, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CloneOutcome.Fail(502, "source-unreachable",
                $"Could not reach the source node at {source}: {ex.GetBaseException().Message}");
        }
        if (disable.StatusCode == HttpStatusCode.Unauthorized)
            return CloneOutcome.Fail(401, "source-auth",
                "The source node rejected the admin password. Nothing was changed.");
        if (!disable.IsSuccessStatusCode)
            return CloneOutcome.Fail(502, "source-error",
                $"The source node refused the disable request ({(int)disable.StatusCode}).");

        // 2. Final full snapshot — everything up to seconds ago.
        var fetched = await FetchSnapshotAsync(source, sourcePassword, includeSecrets: true, ct);
        if (fetched.Outcome is { } fetchError)
            return fetchError with
            {
                Error = fetchError.Error +
                    " The source has been disabled; re-enable it from its Admin page if you are not migrating.",
            };
        var snapshot = fetched.Snapshot!;

        // 3. Import-replace inside one transaction: a failure leaves the
        //    pre-take-over clone intact (and this node still disabled).
        try
        {
            await SnapshotImporter.ImportAsync(dbFactory, snapshot, ct);
        }
        catch (InvalidOperationException ex)
        {
            return CloneOutcome.Fail(400, "import-failed",
                ex.Message + " The source has been disabled; this node is unchanged.");
        }

        // 4. Go live. The warm restart seeds from the just-imported data, so
        //    the hand-off gap is seconds and the timeline stays continuous.
        await settings.SetManyAsync(new Dictionary<string, string?>
        {
            [SettingKeys.EngineEnabled] = "true",
        });
        if (!await host.RebuildAsync())
            return CloneOutcome.Fail(500, "rebuild-failed",
                "Data was final-synced but the engine could not start; see this node's log. The source is disabled.");
        log.LogInformation("Took over from {Source}: {Transitions} transitions final-synced",
            source, snapshot.Transitions.Count);
        return CloneOutcome.Ok(Summary(snapshot, includeSecrets: true));
    }

    private async Task<(Snapshot? Snapshot, CloneOutcome? Outcome)> FetchSnapshotAsync(
        Uri source, string? password, bool includeSecrets, CancellationToken ct)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        try
        {
            HttpResponseMessage response;
            if (includeSecrets)
            {
                response = await client.PostAsJsonAsync(
                    new Uri(source, "api/v1/export/snapshot/full"),
                    new { password }, Json, ct);
            }
            else
            {
                response = await client.GetAsync(new Uri(source, "api/v1/export/snapshot"), ct);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (null, CloneOutcome.Fail(401, "source-auth",
                    "The source node rejected the admin password."));
            if (!response.IsSuccessStatusCode)
                return (null, CloneOutcome.Fail(502, "source-error",
                    $"The source node answered {(int)response.StatusCode} for the snapshot. " +
                    "Is the URL a ConnectionWatcher node?"));

            var snapshot = await response.Content.ReadFromJsonAsync<Snapshot>(Json, ct);
            if (snapshot is null)
                return (null, CloneOutcome.Fail(502, "source-error",
                    "The source node returned an empty snapshot document."));
            return (snapshot, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, CloneOutcome.Fail(502, "source-unreachable",
                $"Could not fetch a snapshot from {source}: {ex.GetBaseException().Message}"));
        }
    }

    private static bool TryParseSource(string? url, out Uri source, out CloneOutcome? error)
    {
        source = null!;
        error = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            error = CloneOutcome.Fail(400, "bad-source-url",
                "The source must be an absolute http(s) URL, e.g. https://conwatch.example.com/ " +
                "or http://192.168.1.5:8080.");
            return false;
        }
        // Endpoint paths are appended relative to the root (or proxied base).
        source = parsed.AbsolutePath.EndsWith('/') ? parsed
            : new UriBuilder(parsed) { Path = parsed.AbsolutePath + "/" }.Uri;
        return true;
    }

    private static object Summary(Snapshot snapshot, bool includeSecrets) => new
    {
        Targets = snapshot.Config.Targets.Count,
        Pools = snapshot.Config.Pools.Count,
        Profiles = snapshot.Config.Profiles.Count,
        Transitions = snapshot.Transitions.Count,
        Rollups = snapshot.Rollups.Count,
        Glitches = snapshot.Glitches.Count,
        SecretsTransferred = includeSecrets,
        RedactedSettings = snapshot.RedactedSettings,
    };
}
