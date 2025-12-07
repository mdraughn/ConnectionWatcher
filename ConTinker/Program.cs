using ConInfrastructure;
using ConEngine.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Threading;
using System.ComponentModel.DataAnnotations;
using ConCore;
using ConEngine;
using ConTinker;
using Serilog;
using ConTinker.Configuration;

Console.WriteLine("Program Running");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    IHostBuilder hostBuilder = Host.CreateDefaultBuilder();
    
    hostBuilder.ConfigureServices((context, services) =>
    {
        // Log the environment and configuration sources
        var environment = context.HostingEnvironment.EnvironmentName;
        Log.Information("Running in environment: {Environment}", environment);
        Log.Information("Application name: {ApplicationName}", context.HostingEnvironment.ApplicationName);
        Log.Information("Content root: {ContentRoot}", context.HostingEnvironment.ContentRootPath);
        
        // Configure HostTrackingOptions with validation
        services.AddOptions<HostTrackingOptions>()
            .Bind(context.Configuration.GetSection("HostTracking"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<HostTrackingOptions>, HostTrackingOptionsValidator>();
        
        // Configure PingTransceiverOptions with validation
        services.AddOptions<PingTransceiverOptions>()
            .Bind(context.Configuration.GetSection(PingTransceiverOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PingTransceiverOptions>, PingTransceiverOptionsValidator>();
    });
    
    hostBuilder.UseSerilog();
    
    hostBuilder.ConfigureServices((context, services) =>
    {
        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddTransient<IDomainNameResolverFactory, DomainNameResolverFactory>();
        
        // Use configuration-based factory instead of hardcoded simulation
        services.AddSingleton<IPingTransceiverFactory, ConfiguredPingTransceiverFactory>();
        
        services.AddTransient<WatcherEngine>();
        services.AddHostedService<TinkerService>();
    });
    
    using var host = hostBuilder.Build();
    
    // Validate options on startup
    var hostTrackingOptions = host.Services.GetRequiredService<IOptions<HostTrackingOptions>>();
    var hostTracking = hostTrackingOptions.Value;
    
    var pingTransceiverOptions = host.Services.GetRequiredService<IOptions<PingTransceiverOptions>>();
    var pingTransceiver = pingTransceiverOptions.Value;
    
    Log.Information("Starting with HostTracking configuration: HistorySize={HistorySize}, MinUsableSample={MinUsableSample}",
        hostTracking.HistorySize, hostTracking.MinUsableSample);
    
    Log.Information("PingTransceiver configuration: UseSimulation={UseSimulation}, Preset={Preset}, ShowControlWindow={ShowControlWindow}",
        pingTransceiver.UseSimulation, pingTransceiver.SimulationPreset, pingTransceiver.ShowControlWindow);
    
    await host.RunAsync(CancellationToken.None);
}
catch (OptionsValidationException ex)
{
    Log.Fatal(ex, "Configuration validation failed: {Failures}", string.Join("; ", ex.Failures));
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

Console.WriteLine("Program Completed");
