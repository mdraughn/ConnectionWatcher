using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// Exhaustive lock contract: every mapped /api/v1 route is probed with the
/// gate on and no admin cookie. Routes on the open allowlist must answer;
/// everything else must 401. A new endpoint appears in neither bucket by
/// accident — it fails this test until someone classifies it, so "forgot to
/// lock the new endpoint" cannot merge quietly.
/// </summary>
public class ApiSurfaceLockTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // Deliberately open surfaces (watching + auth itself). Prefix match.
    private static readonly string[] OpenPrefixes =
    [
        "/api/v1/status",
        "/api/v1/transitions",
        "/api/v1/rollups",
        "/api/v1/export",
        "/api/v1/health",
        "/api/v1/config/health",
        "/api/v1/auth/",
    ];

    // Open, but legitimately return 401 on bad credentials — probing them
    // with a junk body would be a false positive. Covered by AuthApiTests.
    private static readonly string[] SkipProbe =
        ["/api/v1/auth/login", "/api/v1/auth/remove-password", "/api/v1/auth/set-password"];

    // Bodies that survive model binding for DTOs with `required` members;
    // binding runs BEFORE endpoint filters, so an unbindable body would 400
    // and never reach the 401 lock. The filter still rejects before the
    // handler runs, so these never touch the database.
    private static string SampleBody(string url) => url switch
    {
        _ when url.Contains("/targets") => """{"name":"probe","address":"192.0.2.1"}""",
        _ when url.Contains("/profiles") => """{"name":"probe"}""",
        _ when url.Contains("/pools") => """{"name":"probe"}""",
        _ => "{}",
    };

    [Fact]
    public async Task Every_api_route_is_classified_and_behaves_when_locked_out()
    {
        // Gate on; the probing client carries no cookie.
        var adminClient = _factory.CreateClient();
        await adminClient.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" });
        var anon = _factory.CreateClient();

        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && raw.StartsWith("/api/v1"))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (Method: m, Route: e.RoutePattern.RawText!)))
            .Distinct()
            .ToList();

        Assert.NotEmpty(routes); // the enumeration itself must work

        var failures = new List<string>();
        foreach (var (method, route) in routes)
        {
            // Substitute route parameters ({id}, {id:guid}, ...) with a GUID;
            // locked routes 401 before the value matters, open ones may 404.
            var url = Regex.Replace(route, @"\{[^}]+\}", Guid.NewGuid().ToString());
            if (SkipProbe.Contains(url))
                continue;

            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent(SampleBody(url), Encoding.UTF8, "application/json");

            var response = await anon.SendAsync(request);
            var open = OpenPrefixes.Any(url.StartsWith);
            if (open && response.StatusCode == HttpStatusCode.Unauthorized)
                failures.Add($"{method} {route} is on the open allowlist but returned 401");
            else if (!open && response.StatusCode != HttpStatusCode.Unauthorized)
                failures.Add($"{method} {route} should be locked but returned {(int)response.StatusCode} " +
                             "(new endpoint? map it on the locked group, or add it to OpenPrefixes here)");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
