using ConHost;
using Microsoft.Extensions.Logging;

Console.WriteLine("ConDiag - Connection Diagnostics Tool");
Console.WriteLine("Starting ping monitoring for 8.8.8.8...");
Console.WriteLine("WebAPI will be available at http://localhost:5000/api");
Console.WriteLine("Web UI will be available at http://localhost:5000");
Console.WriteLine("Press Ctrl+C to stop.");
Console.WriteLine();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Add services
    builder.Services.AddControllers();
    builder.Services.AddRazorPages();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    
    // Configure logging
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Information);

    var app = builder.Build();

    // Configure the HTTP request pipeline
    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseHttpsRedirection();
    app.UseStaticFiles(); // Enable static files for CSS, JS, etc.
    app.UseRouting();
    app.UseAuthorization();
    
    // Map controllers (API endpoints)
    app.MapControllers();
    
    // Map Razor Pages
    app.MapRazorPages();

    // Add simple API endpoints
    app.MapGet("/api", () => "ConDiag API is running");
    
    app.MapGet("/api/health", () => new { 
        Status = "Healthy", 
        Timestamp = DateTime.UtcNow,
        Service = "ConDiag Connection Diagnostics"
    });

    app.MapGet("/api/ping-status", () => new {
        Message = "Ping worker is running in background",
        Target = "8.8.8.8",
        Interval = "5 seconds"
    });

    await app.RunAsync();
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nApplication stopped by user.");
}
catch (Exception ex)
{
    Console.WriteLine($"\nError: {ex.Message}");
}
