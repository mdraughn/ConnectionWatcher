using ConCore;
using ConEngine.Configuration;
using ConInfrastructure;
using ConPersistence;
using ConPersistence.Services;
using ConWatch.Auth;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConWatch;

/// <summary>
/// Service registration for the ConWatch host, one method per concern, so
/// Program.cs reads as a table of contents and each ordering rule lives next
/// to the registrations it governs.
/// </summary>
public static class ConWatchServices
{
    public static IServiceCollection AddConWatchOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.Configure<VerdictOptions>(configuration.GetSection(VerdictOptions.SectionName));
        return services;
    }

    /// <summary>
    /// SQLite at Persistence:DbPath (default data/conwatch.db). Returns the
    /// database's directory: the admin password file and Data Protection keys
    /// live beside it so one folder holds all mutable state.
    /// </summary>
    public static IServiceCollection AddConWatchDatabase(this IServiceCollection services,
        IConfiguration configuration, out string stateDir)
    {
        var dbPath = configuration[$"{PersistenceOptions.SectionName}:DbPath"] ?? "data/conwatch.db";
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        stateDir = dbDir is { Length: > 0 } ? dbDir : ".";
        Directory.CreateDirectory(stateDir);
        services.AddDbContextFactory<ConWatchDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        return services;
    }

    public static IServiceCollection AddConWatchEngine(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IPingTransceiverFactory, PingTransceiverFactoryIcmp>();
        services.AddSingleton<IDomainNameResolverFactory, DomainNameResolverFactory>();
        services.AddSingleton<ConInfrastructure.Traceroute.ITtlProbe, ConInfrastructure.Traceroute.IcmpTtlProbe>();
        services.AddSingleton<ConInfrastructure.Traceroute.TracerouteRunner>();
        services.AddSingleton<ConInfrastructure.Discovery.IGatewayDiscovery, ConInfrastructure.Discovery.GatewayDiscovery>();
        services.AddSingleton<ConInfrastructure.Discovery.IWanAddressProbe, ConInfrastructure.Discovery.UpnpWanAddressProbe>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<WatcherHostService>();
        return services;
    }

    public static IServiceCollection AddConWatchPersistence(this IServiceCollection services)
    {
        services.AddSingleton<ConfigStore>();
        services.AddSingleton<ConfigService>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ConfigHealth>();
        services.AddSingleton<ConPersistence.Report.IspReportBuilder>();
        services.AddSingleton<TransitionRecorder>();
        services.AddSingleton<GlitchRecorder>();
        services.AddSingleton<RollupCollector>();
        return services;
    }

    /// <summary>Cross-node clone/take-over: outbound calls to the source node.</summary>
    public static IServiceCollection AddConWatchClone(this IServiceCollection services)
    {
        services.AddHttpClient(Clone.CloneService.HttpClientName,
            c => c.Timeout = TimeSpan.FromSeconds(60)); // a full snapshot can be MBs
        services.AddSingleton<Clone.CloneService>();
        services.AddSingleton<Clone.CloneHealth>();
        return services;
    }

    /// <summary>
    /// Notifiers: idle until Mqtt.Enabled / Webhook.Enabled; reconfigured live
    /// via NotifierSettingsChanged, re-attached by WatcherHostService on every rebuild.
    /// </summary>
    public static IServiceCollection AddConWatchNotifiers(this IServiceCollection services)
    {
        services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<ConNotify.MqttNotifier>();
        services.AddSingleton<ConNotify.WebhookNotifier>();
        return services;
    }

    /// <summary>
    /// Admin gate: password file lives beside the database; cookies mark a
    /// browser as admin for the session. Keys persisted so cookies survive a
    /// service restart.
    /// </summary>
    public static IServiceCollection AddConWatchAdminAuth(this IServiceCollection services, string stateDir)
    {
        services.AddSingleton(sp => new AdminGate(
            Path.Combine(stateDir, "admin-password.txt"),
            sp.GetRequiredService<ILogger<AdminGate>>()));
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(stateDir, "keys")));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "conwatch.admin";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                // APIs get plain status codes, never login-page redirects.
                o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
                o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
            });
        return services;
    }

    /// <summary>Web surfaces: MCP, OpenAPI, Razor Pages (admin lock by convention), SignalR, metrics.</summary>
    public static IServiceCollection AddConWatchWeb(this IServiceCollection services)
    {
        // MCP endpoint (/mcp): read-only tools over the open watching surface, so an
        // MCP host can query status and history directly. Stateless HTTP keeps it
        // session-free and reverse-proxy-friendly.
        services.AddMcpServer(o => o.ServerInfo = new()
            {
                Name = "ConnectionWatcher",
                Version = BuildInfo.Version,
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<Mcp.ConWatchMcpTools>();

        services.AddOpenApi();
        services.AddRazorPages(o =>
        {
            // Admin pages: one filter by convention instead of a check in every model.
            var adminLock = new TypeFilterAttribute(typeof(AdminLockPageFilter));
            o.Conventions.AddFolderApplicationModelConvention("/Config", m => m.Filters.Add(adminLock));
            foreach (var page in new[] { "/Admin", "/Data", "/Setup", "/Traceroute" })
                o.Conventions.AddPageApplicationModelConvention(page, m => m.Filters.Add(adminLock));
        });
        services.AddSignalR();
        services.AddSingleton<StatusBroadcaster>();
        services.AddSingleton<Metrics.MetricsCounters>();
        return services;
    }

    /// <summary>
    /// Hosted services start in registration order and stop in reverse. The
    /// consumers (recorders, retention, notifiers, broadcaster) come first so
    /// they are listening before the engine emits; the engine host comes LAST
    /// so it stops before any consumer closes its channel. Schema migration
    /// happens before any of them, in DatabaseStartup.PrepareAsync.
    /// </summary>
    public static IServiceCollection AddConWatchHostedServices(this IServiceCollection services)
    {
        services.AddHostedService(sp => sp.GetRequiredService<TransitionRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<GlitchRecorder>());
        services.AddHostedService(sp => sp.GetRequiredService<RollupCollector>());
        services.AddHostedService<RetentionService>();
        services.AddHostedService(sp => sp.GetRequiredService<ConNotify.MqttNotifier>());
        services.AddHostedService(sp => sp.GetRequiredService<ConNotify.WebhookNotifier>());
        services.AddHostedService<ConNotify.HeartbeatNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<StatusBroadcaster>());
        services.AddHostedService(sp => sp.GetRequiredService<WatcherHostService>());
        return services;
    }
}
