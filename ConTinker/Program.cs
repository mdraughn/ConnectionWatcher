using ConInfrastructure;
using ConEngine.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Threading;
using ConCore;
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
    hostBuilder.UseSerilog();
        //.ConfigureLogging(logging =>
        //{
        //    logging.ClearProviders();
        //    logging.AddConsole();
        //    logging.SetMinimumLevel(LogLevel.Information);
        //});
    hostBuilder.ConfigureServices((context, services) =>
    {
        // Configure HostTrackingOptions with validation
        services.AddOptions<HostTrackingOptions>()
            .Bind(context.Configuration.GetSection(HostTrackingOptions.SectionName))
            .ValidateOnStart();
        
        // Register the validator
        services.AddSingleton<IValidateOptions<HostTrackingOptions>, HostTrackingOptionsValidator>();
        
        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddTransient<IDomainNameResolverFactory, DomainNameResolverFactory>();
        //services.AddTransient<IPingTransceiverFactory, PingTransceiverFactoryIcmp>();
        services.AddTransient<IPingTransceiverFactory>(sp => PingTransceiverFactorySimulated.CreateUnreliableNetwork());
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
