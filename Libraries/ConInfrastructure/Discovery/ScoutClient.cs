using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConInfrastructure.Net;

namespace ConInfrastructure.Discovery;

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
/// </summary>
public sealed class ScoutClient(HttpClient http, Uri server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private Uri Url(string path) => new(server.AbsolutePath.EndsWith('/') ? server : new Uri(server + "/"), path);

    public async Task<ScoutResult> SendAsync(IReadOnlyList<ScoutItem> items, string? adminPassword,
        bool allowInsecure, CancellationToken ct)
    {
        try
        {
            var response = await PostItemsAsync(items, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (adminPassword is null)
                    return ScoutResult.Fail(ScoutFailure.PasswordRequired,
                        "This server needs its admin password before it will accept discoveries.");
                // The password is about to cross the wire: decide now, before it does.
                if (TransportPolicy.Evaluate(server, sensitive: true, allowInsecure) == TransportPolicy.Verdict.RequiresOverride)
                    return ScoutResult.Fail(ScoutFailure.InsecureTransport,
                        $"Signing in would send your admin password to {server.Host} unencrypted over plain HTTP.");

                var login = await http.PostAsJsonAsync(Url("api/v1/auth/login"), new { password = adminPassword }, Json, ct);
                if (login.StatusCode == HttpStatusCode.Unauthorized)
                    return ScoutResult.Fail(ScoutFailure.WrongPassword, "The admin password was not accepted.");
                if (!login.IsSuccessStatusCode)
                    return ScoutResult.Fail(ScoutFailure.ServerError,
                        $"The server refused the sign-in ({(int)login.StatusCode}).");
                response = await PostItemsAsync(items, ct);
            }
            return await InterpretAsync(response, ct);
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

    private Task<HttpResponseMessage> PostItemsAsync(IReadOnlyList<ScoutItem> items, CancellationToken ct) =>
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
