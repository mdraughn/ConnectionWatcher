using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using static ConWatch.Tests.SurfaceClassificationTests;

namespace ConWatch.Tests;

/// <summary>
/// Lock contract for every mapped endpoint (not only /api/v1), probed with
/// the gate on and no admin cookie against the one table in
/// <see cref="SurfaceClassificationTests.Surface"/>: open endpoints must
/// answer, locked ones must 401, and the inline-verified ones must 401 a
/// wrong password. An endpoint missing from the table fails here too.
/// </summary>
public class ApiSurfaceLockTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

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

    // The inline-verified routes, each with a wrong password where it checks one.
    private static readonly Dictionary<string, string> WrongPassword = new()
    {
        ["/api/v1/auth/login"] = """{"password":"wrong"}""",
        ["/api/v1/auth/set-password"] = """{"current":"wrong","password":"new-pass"}""",
        ["/api/v1/auth/remove-password"] = """{"current":"wrong"}""",
        ["/api/v1/export/snapshot/full"] = """{"password":"wrong"}""",
        ["/api/v1/admin/engine"] = """{"enabled":false,"password":"wrong"}""",
    };

    [Fact]
    public async Task Every_api_route_is_classified_and_behaves_when_locked_out()
    {
        // Gate on; the probing client carries no cookie and does not follow redirects.
        var adminClient = _factory.CreateClient();
        (await adminClient.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var routes = Endpoints(_factory.Services);
        Assert.NotEmpty(routes); // the enumeration itself must work

        var failures = new List<string>();
        foreach (var (method, route) in routes)
        {
            if (!Surface.TryGetValue(KeyOf(method, route), out var access))
            {
                failures.Add($"{method} {route} is not classified (add it to SurfaceClassificationTests.Surface)");
                continue;
            }

            // Substitute route parameters ({id}, {id:guid}, ...) with a GUID;
            // locked routes 401 before the value matters, open ones may 404.
            var url = Regex.Replace(route, @"\{[^}]+\}", Guid.NewGuid().ToString());
            var verb = method == "*" ? "GET" : method;
            var request = new HttpRequestMessage(new HttpMethod(verb), url);
            if (access == Access.OpenInlineVerified)
                request.Content = new StringContent(WrongPassword[url], Encoding.UTF8, "application/json");
            else if (verb is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent(SampleBody(url), Encoding.UTF8, "application/json");

            var response = await anon.SendAsync(request);
            var code = (int)response.StatusCode;
            switch (access)
            {
                case Access.OpenWatch when response.StatusCode == HttpStatusCode.Unauthorized:
                    failures.Add($"{method} {route} is open but returned 401");
                    break;
                case Access.OpenInlineVerified when response.StatusCode != HttpStatusCode.Unauthorized:
                    failures.Add($"{method} {route} verifies the password inline but answered {code} to a wrong one");
                    break;
                case Access.Locked when response.StatusCode != HttpStatusCode.Unauthorized:
                    failures.Add($"{method} {route} should be locked but returned {code}");
                    break;
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
