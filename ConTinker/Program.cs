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

Console.WriteLine("Program Running");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    IHostBuilder hostBuilder = Host.CreateDefaultBuilder();
    // Configure HostTrackingOptions with validation
    hostBuilder.ConfigureServices((context, services) =>
    {
        // Configure HostTrackingOptions with validation
        services.AddOptions<HostTrackingOptions>()
            .Bind(context.Configuration.GetSection("MyFeature"))
            .ValidateOnStart();
        // Register the validator
        services.AddSingleton<IValidateOptions<HostTrackingOptions>, HostTrackingOptionsValidator>();
    });
    hostBuilder.UseSerilog();
        //.ConfigureLogging(logging =>
        //{
        //    logging.ClearProviders();
        //    logging.AddConsole();
        //    logging.SetMinimumLevel(LogLevel.Information);
        //});
    hostBuilder.ConfigureServices((context, services) =>
    {
        
        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddTransient<IDomainNameResolverFactory, DomainNameResolverFactory>();
        services.AddSingleton<IPingTransceiverFactory>(sp => PingTransceiverInteractiveSimFactory.CreateUnreliableNetwork());
        services.AddTransient<WatcherEngine>();
        services.AddHostedService<TinkerService>();
    });
    using var host = hostBuilder.Build();
    
    // Validate options on startup
    var optionsValidator = host.Services.GetRequiredService<IOptions<HostTrackingOptions>>();
    var options = optionsValidator.Value; // This will throw if validation fails
    
    Log.Information("Starting with HostTracking configuration: HistorySize={HistorySize}, MinUsableSample={MinUsableSample}",
        options.HistorySize, options.MinUsableSample);
    
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
