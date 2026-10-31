using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Net;

namespace ConInfrastructure.Tray;

public sealed record ScoutItem(string Address, string? Hostname, DiscoveryKind Kind, DiscoveryMethod Method,
    string Origin, string? Evidence);

public enum ScoutFailure
{
    None,
    Unreachable,
    PasswordRequired,
    WrongPassword,
    InsecureTransport,
    Rejected,
    ServerError,
}

public sealed record ScoutResult(bool Success, ScoutFailure Failure, string Message, int Created = 0, int Updated = 0)
{
    public static ScoutResult Fail(ScoutFailure failure, string message) => new(false, failure, message);
}

/// <summary>
/// The tray scout's HTTP side: post discoveries to a node, signing in with the admin password only when
/// the node answers 401, and never sending that password over plain HTTP to another machine without an
/// explicit yes (<see cref="TransportPolicy"/>). The caller supplies an <see cref="HttpClient"/> with
/// cookies enabled, so one sign-in covers later sends. Messages never contain the password.
/// Any 3xx answer is treated as a failure and never followed by this class, but a redirect that the
/// <see cref="HttpClient"/> itself follows would re-send the password to the target unchecked, so the
/// caller MUST build its handler with <c>AllowAutoRedirect = false</c>.
/// </summary>
public sealed class ScoutClient(HttpClient http, Uri server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The node this client posts to, exactly as it was given.</summary>
    public Uri Server => server;

    /// <summary>
    /// The transport this class requires: redirects are never followed (a redirect must not re-send the
    /// admin password anywhere) and cookies are kept in the handler's own container so one sign-in
    /// covers later sends. Build the caller's <see cref="HttpClient"/> from this.
    /// </summary>
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    private Uri Url(string path) => new(server.AbsolutePath.EndsWith('/') ? server : new Uri(server + "/"), path);

    /// <summary>
    /// Posts the items in batches of at most <see cref="DiscoveryLimits.MaxBatch"/> (the server's per-request
    /// cap), signing in at most once. The totals are summed; the first failing batch is returned as is.
    /// </summary>
    public async Task<ScoutResult> SendAsync(IReadOnlyList<ScoutItem> items, string? adminPassword,
        bool allowInsecure, CancellationToken ct)
    {
        try
        {
            var signedIn = false;
            int created = 0, updated = 0;
            var batches = items.Count == 0 ? [Array.Empty<ScoutItem>()] : items.Chunk(DiscoveryLimits.MaxBatch).ToList();
            foreach (var batch in batches)
            {
                var response = await PostItemsAsync(batch, ct);
                if (IsRedirect(response)) return Redirected();
                if (response.StatusCode == HttpStatusCode.Unauthorized && !signedIn)
                {
                    if (adminPassword is null)
                        return ScoutResult.Fail(ScoutFailure.PasswordRequired,
                            "This server needs its admin password before it will accept discoveries.");
                    // The password is about to cross the wire: decide now, before it does.
                    if (TransportPolicy.Evaluate(server, sensitive: true, allowInsecure) == TransportPolicy.Verdict.RequiresOverride)
                        return ScoutResult.Fail(ScoutFailure.InsecureTransport,
                            $"Signing in would send your admin password to {server.Host} unencrypted over plain HTTP.");

                    var login = await http.PostAsJsonAsync(Url("api/v1/auth/login"), new { password = adminPassword }, Json, ct);
                    if (IsRedirect(login)) return Redirected();
                    if (login.StatusCode == HttpStatusCode.Unauthorized)
                        return ScoutResult.Fail(ScoutFailure.WrongPassword, "The admin password was not accepted.");
                    if (!login.IsSuccessStatusCode)
                        return ScoutResult.Fail(ScoutFailure.ServerError,
                            $"The server refused the sign-in ({(int)login.StatusCode}).");
                    signedIn = true;
                    response = await PostItemsAsync(batch, ct);
                    if (IsRedirect(response)) return Redirected();
                }
                var result = await InterpretAsync(response, ct);
                if (!result.Success) return result;
                created += result.Created;
                updated += result.Updated;
            }
            return new ScoutResult(true, ScoutFailure.None, "Sent.", created, updated);
        }
        catch (HttpRequestException ex)
        {
            return Unreachable(ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return Unreachable(ex);       // the HttpClient's own timeout
        }
    }
    private static bool IsRedirect(HttpResponseMessage r) => (int)r.StatusCode is >= 300 and <= 399;

    private static ScoutResult Redirected() =>
        ScoutResult.Fail(ScoutFailure.ServerError,
            "The server redirected the request; check the server address in tray settings (for example http vs https).");

    private Task<HttpResponseMessage> PostItemsAsync(IReadOnlyCollection<ScoutItem> items, CancellationToken ct) =>
        http.PostAsJsonAsync(Url("api/v1/discoveries"), new { items }, Json, ct);

    private ScoutResult Unreachable(Exception ex) =>
        ScoutResult.Fail(ScoutFailure.Unreachable,
            $"Could not reach the server at {server.Authority}: {ex.GetBaseException().Message}");

    private static async Task<ScoutResult> InterpretAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);
                return new ScoutResult(true, ScoutFailure.None, "Sent.",
                    json.GetProperty("created").GetInt32(), json.GetProperty("updated").GetInt32());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return ScoutResult.Fail(ScoutFailure.ServerError, "The server answered, but not like a ConnectionWatcher node.");
            }
        }
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var errors = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("errors")
                    .EnumerateArray().Select(e => e.GetString()).Where(e => e is not null);
                return ScoutResult.Fail(ScoutFailure.Rejected, "The server rejected the results: " + string.Join(" ", errors));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return ScoutResult.Fail(ScoutFailure.Rejected, "The server rejected the results.");
            }
        }
        return ScoutResult.Fail(ScoutFailure.ServerError, $"The server answered {(int)response.StatusCode}.");
    }
}
