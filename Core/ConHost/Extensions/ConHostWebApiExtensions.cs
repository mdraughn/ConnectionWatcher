using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConHost.Extensions;

/// <summary>
/// Extensions for ConHostEngine to add WebAPI capabilities.
/// </summary>
public static class ConHostWebApiExtensions
{
    /// <summary>
    /// Adds a WebAPI server as a hosted service to the ConHostEngine.
    /// </summary>
    /// <param name="engine">The ConHostEngine instance.</param>
    /// <param name="configureWebApp">Optional action to configure the WebApplication.</param>
    /// <param name="urls">The URLs to bind to (defaults to http://localhost:5000).</param>
    /// <returns>The ConHostEngine instance for method chaining.</returns>
    public static ConHostEngine AddWebApi(this ConHostEngine engine, 
        Action<WebApplication>? configureWebApp = null,
        params string[] urls)
    {
        urls = urls.Length == 0 ? ["http://localhost:5000"] : urls;

        return engine.ConfigureServices(services =>
        {
            // Add the WebAPI as a hosted service
            services.AddHostedService(serviceProvider => 
                new WebApiHostedService(serviceProvider, configureWebApp, urls));
        });
    }
}

/// <summary>
/// Hosted service that runs a WebAPI server within the ConHostEngine.
/// </summary>
internal class WebApiHostedService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Action<WebApplication>? _configureWebApp;
    private readonly string[] _urls;
    private WebApplication? _webApp;
    private readonly ILogger<WebApiHostedService> _logger;

    public WebApiHostedService(
        IServiceProvider serviceProvider, 
        Action<WebApplication>? configureWebApp,
        string[] urls)
    {
        _serviceProvider = serviceProvider;
        _configureWebApp = configureWebApp;
        _urls = urls;
        _logger = serviceProvider.GetRequiredService<ILogger<WebApiHostedService>>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting WebAPI server on {Urls}", string.Join(", ", _urls));

        var builder = WebApplication.CreateBuilder();
        
        // Configure services
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // Copy logging configuration from parent
        var parentLoggingBuilder = _serviceProvider.GetService<ILoggingBuilder>();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();

        // Configure URLs
        builder.WebHost.UseUrls(_urls);

        _webApp = builder.Build();

        // Configure the HTTP request pipeline
        if (_webApp.Environment.IsDevelopment())
        {
            _webApp.UseSwagger();
            _webApp.UseSwaggerUI();
        }

        _webApp.UseHttpsRedirection();
        _webApp.UseAuthorization();
        _webApp.MapControllers();

        // Default endpoints
        _webApp.MapGet("/", () => "ConHost WebAPI is running");
        _webApp.MapGet("/health", () => new { Status = "Healthy", Timestamp = DateTime.UtcNow });

        // Allow custom configuration
        _configureWebApp?.Invoke(_webApp);

        // Start the web application
        await _webApp.StartAsync(cancellationToken);
        
        _logger.LogInformation("WebAPI server started successfully");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping WebAPI server");
        
        if (_webApp != null)
        {
            await _webApp.StopAsync(cancellationToken);
            await _webApp.DisposeAsync();
        }
        
        _logger.LogInformation("WebAPI server stopped");
    }
}