using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>
/// The one classification of the whole HTTP surface: every mapped endpoint
/// (by exact method and raw route pattern) and every Razor page (by its view
/// path) sits in exactly one bucket of <see cref="Surface"/>. A new route or
/// page that nobody classified fails here by name, so "forgot to lock it"
/// cannot merge quietly — and neither can a table row for something that no
/// longer exists. <see cref="ApiSurfaceLockTests"/> probes the endpoints
/// against this same table; this class checks the pages.
/// </summary>
public class SurfaceClassificationTests : IDisposable
{
    internal enum Access
    {
        /// <summary>Deliberately open with a password set: watching, auth itself, transports.</summary>
        OpenWatch,
        /// <summary>Open route that checks the admin password itself (401 on a wrong one).</summary>
        OpenInlineVerified,
        /// <summary>401 (API) or redirect to the dashboard prompt (page) while locked.</summary>
        Locked,
    }

    private const string PageKey = "PAGE";

    /// <summary>
    /// Key: "METHOD raw-pattern" for endpoints ("*" when the endpoint carries
    /// no HttpMethodMetadata, i.e. it accepts any method), "PAGE view-path"
    /// for Razor pages. Exact matches only — no prefixes.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, Access> Surface = new Dictionary<string, Access>
    {
        // ---- Open: watching (read-only) ----
        ["GET /api/v1/status"] = Access.OpenWatch,
        ["GET /api/v1/health"] = Access.OpenWatch,
        ["GET /api/v1/transitions"] = Access.OpenWatch,
        ["GET /api/v1/rollups/{targetId:guid}"] = Access.OpenWatch,
        ["GET /api/v1/glitches"] = Access.OpenWatch,
        ["GET /api/v1/export/transitions"] = Access.OpenWatch,
        ["GET /api/v1/export/rollups"] = Access.OpenWatch,
        ["GET /api/v1/export/snapshot"] = Access.OpenWatch,      // secrets redacted
        ["GET /api/v1/export/glitches"] = Access.OpenWatch,
        ["GET /api/v1/config/health"] = Access.OpenWatch,
        ["GET /metrics"] = Access.OpenWatch,
        ["GET /openapi/{documentName}.json"] = Access.OpenWatch,
        ["GET /Admin"] = Access.OpenWatch,                       // permanent redirect to /Settings (which is locked)
        // Transports: POST by protocol, not mutations. The MCP tools are all
        // ReadOnly and list_discoveries follows the gate (McpEndpointTests);
        // the status hub has no client-invokable methods.
        ["POST /mcp/"] = Access.OpenWatch,
        ["* /hubs/status/negotiate"] = Access.OpenWatch,
        ["* /hubs/status"] = Access.OpenWatch,
        // Auth itself: status is read-only; logout only drops the caller's own cookie.
        ["GET /api/v1/auth/status"] = Access.OpenWatch,
        ["POST /api/v1/auth/logout"] = Access.OpenWatch,

        // ---- Open, password verified inline (AuthApiTests, CloneTests) ----
        ["POST /api/v1/auth/login"] = Access.OpenInlineVerified,
        ["POST /api/v1/auth/set-password"] = Access.OpenInlineVerified,
        ["POST /api/v1/auth/remove-password"] = Access.OpenInlineVerified,
        ["POST /api/v1/export/snapshot/full"] = Access.OpenInlineVerified,
        ["POST /api/v1/admin/engine"] = Access.OpenInlineVerified,

        // ---- Locked: configuration ----
        ["GET /api/v1/targets/"] = Access.Locked,
        ["GET /api/v1/targets/{id:guid}"] = Access.Locked,
        ["POST /api/v1/targets/"] = Access.Locked,
        ["PUT /api/v1/targets/{id:guid}"] = Access.Locked,
        ["DELETE /api/v1/targets/{id:guid}"] = Access.Locked,
        ["POST /api/v1/targets/insert-parent"] = Access.Locked,
        ["POST /api/v1/targets/probe-test"] = Access.Locked,
        ["GET /api/v1/profiles/"] = Access.Locked,
        ["GET /api/v1/profiles/{id:guid}"] = Access.Locked,
        ["POST /api/v1/profiles/"] = Access.Locked,
        ["PUT /api/v1/profiles/{id:guid}"] = Access.Locked,
        ["DELETE /api/v1/profiles/{id:guid}"] = Access.Locked,
        ["GET /api/v1/pools/"] = Access.Locked,
        ["GET /api/v1/pools/{id:guid}"] = Access.Locked,
        ["POST /api/v1/pools/"] = Access.Locked,
        ["PUT /api/v1/pools/{id:guid}"] = Access.Locked,
        ["DELETE /api/v1/pools/{id:guid}"] = Access.Locked,
        ["GET /api/v1/config/export"] = Access.Locked,
        ["POST /api/v1/config/import"] = Access.Locked,
        ["GET /api/v1/settings"] = Access.Locked,
        ["PUT /api/v1/settings"] = Access.Locked,
        // ---- Locked: tools, discoveries, notes ----
        ["POST /api/v1/traceroute"] = Access.Locked,
        ["GET /api/v1/discoveries/"] = Access.Locked,
        ["POST /api/v1/discoveries/"] = Access.Locked,
        ["POST /api/v1/discoveries/search/router"] = Access.Locked,
        ["POST /api/v1/discoveries/search/lan"] = Access.Locked,
        ["POST /api/v1/discoveries/search/edge"] = Access.Locked,
        ["POST /api/v1/discoveries/restore-dismissed"] = Access.Locked,
        ["POST /api/v1/discoveries/clear"] = Access.Locked,
        ["DELETE /api/v1/discoveries/{id:guid}"] = Access.Locked,
        ["POST /api/v1/discoveries/{id:guid}/dismiss"] = Access.Locked,
        ["POST /api/v1/discoveries/{id:guid}/restore"] = Access.Locked,
        ["POST /api/v1/warnings/{key}/acknowledge"] = Access.Locked,
        // ---- Locked: data management ----
        ["GET /api/v1/admin/stats"] = Access.Locked,
        ["POST /api/v1/admin/mqtt-test"] = Access.Locked,
        ["POST /api/v1/admin/prune"] = Access.Locked,
        ["POST /api/v1/admin/clear-history"] = Access.Locked,
        ["POST /api/v1/admin/factory-reset"] = Access.Locked,
        // ---- Locked: setup wizard, clone and take-over ----
        ["GET /api/v1/setup/gateway"] = Access.Locked,
        ["GET /api/v1/setup/anchors"] = Access.Locked,
        ["POST /api/v1/setup/wan-probe"] = Access.Locked,
        ["POST /api/v1/setup/apply"] = Access.Locked,
        ["POST /api/v1/setup/clone"] = Access.Locked,
        ["POST /api/v1/setup/takeover"] = Access.Locked,

        // ---- Pages ----
        ["PAGE /Index"] = Access.OpenWatch,
        ["PAGE /History"] = Access.OpenWatch,
        ["PAGE /Report"] = Access.OpenWatch,
        ["PAGE /Settings"] = Access.Locked,
        ["PAGE /Data"] = Access.Locked,
        ["PAGE /Setup"] = Access.Locked,
        ["PAGE /Traceroute"] = Access.Locked,
        ["PAGE /Config/Targets"] = Access.Locked,
        ["PAGE /Config/Profiles"] = Access.Locked,
        ["PAGE /Config/Pools"] = Access.Locked,
        ["PAGE /Config/Dns"] = Access.Locked,
        ["PAGE /Config/Discoveries"] = Access.Locked,
    };

    /// <summary>Every non-page route endpoint as (method, raw pattern); "*" = any method.
    /// Razor page endpoints are left to <see cref="Pages"/>.</summary>
    internal static List<(string Method, string Route)> Endpoints(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<PageActionDescriptor>() is null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => (Method: m, Route: e.RoutePattern.RawText ?? "")))
            .Distinct()
            .ToList();

    /// <summary>Every Razor page selector as (view path, URL); a page with two routes
    /// (Index: "/" and "/Index") appears twice.</summary>
    internal static List<(string Page, string Url)> Pages(IServiceProvider services) =>
        services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<PageActionDescriptor>()
            .Select(p => (Page: p.ViewEnginePath, Url: "/" + p.AttributeRouteInfo?.Template))
            .Distinct()
            .ToList();

    internal static string KeyOf(string method, string route) => $"{method} {route}";

    /// <summary>Surface entries the table does not classify, by key.</summary>
    internal static List<string> Unclassified(IServiceProvider services) =>
        Endpoints(services).Select(e => KeyOf(e.Method, e.Route))
            .Concat(Pages(services).Select(p => KeyOf(PageKey, p.Page)))
            .Distinct()
            .Where(key => !Surface.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToList();

    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Every_endpoint_and_page_is_classified()
    {
        var admin = _factory.CreateClient();
        (await admin.PostAsJsonAsync("/api/v1/auth/set-password", new { password = "hunter2" })).EnsureSuccessStatusCode();
        var anon = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var endpoints = Endpoints(_factory.Services);
        var pages = Pages(_factory.Services);
        Assert.NotEmpty(endpoints); // the enumerations themselves must work
        Assert.NotEmpty(pages);

        var unclassified = Unclassified(_factory.Services);
        Assert.True(unclassified.Count == 0,
            "Unclassified surface (classify each in SurfaceClassificationTests.Surface):\n" + string.Join("\n", unclassified));

        var present = endpoints.Select(e => KeyOf(e.Method, e.Route))
            .Concat(pages.Select(p => KeyOf(PageKey, p.Page))).ToHashSet();
        var stale = Surface.Keys.Where(k => !present.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "Classified but no longer mapped (remove from the table):\n" + string.Join("\n", stale));

        // Pages, with a password set and no admin cookie: open pages render,
        // every other page bounces to the dashboard's password prompt.
        var failures = new List<string>();
        foreach (var (page, url) in pages)
        {
            var response = await anon.GetAsync(url);
            var location = response.Headers.Location?.ToString();
            switch (Surface[KeyOf(PageKey, page)])
            {
                case Access.OpenWatch when response.StatusCode != HttpStatusCode.OK:
                    failures.Add($"{page} ({url}) is open but answered {(int)response.StatusCode} {location}");
                    break;
                case Access.Locked when response.StatusCode != HttpStatusCode.Redirect
                                        || location != ConWatch.Auth.AdminLockPageFilter.RedirectTarget:
                    failures.Add($"{page} ({url}) should redirect to /?admin=1 but answered {(int)response.StatusCode} {location}");
                    break;
                case Access.OpenInlineVerified:
                    failures.Add($"{page} is a page; pages are either OpenWatch or Locked");
                    break;
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Maps an extra route outside Program, the way a forgotten new endpoint would appear.</summary>
    private sealed class ExtraRoute : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UseRouting();
            app.UseEndpoints(e => e.MapPost("/api/v1/export/zz", () => Results.Ok("zz")));
            next(app);
        };
    }

    [Fact]
    public async Task A_route_missing_from_the_table_is_named()
    {
        using var factory = new ConWatchFactory
        {
            TestServices = s => s.AddSingleton<IStartupFilter, ExtraRoute>(),
        };
        var client = factory.CreateClient();
        // Really mapped, and under a prefix the old allowlist would have waved through.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/export/zz", null)).StatusCode);

        Assert.Equal(["POST /api/v1/export/zz"], Unclassified(factory.Services));
    }
}
