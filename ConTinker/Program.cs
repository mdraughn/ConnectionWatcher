using ConInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    hostBuilder.ConfigureServices(services =>
    {
        services.AddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.AddTransient<IDomainNameResolverFactory, DomainNameResolverFactory>();
        //services.AddTransient<IPingTransceiverFactory, PingTransceiverFactoryIcmp>();
        services.AddTransient<IPingTransceiverFactory>(sp => PingTransceiverFactorySimulated.CreateUnreliableNetwork());
        services.AddHostedService<TinkerService>();
    });
    using var host = hostBuilder.Build();
    await host.RunAsync(CancellationToken.None);
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
