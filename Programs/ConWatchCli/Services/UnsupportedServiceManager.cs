namespace ConWatchCli.Services;

/// <summary>Every non-Windows OS until a systemd implementation exists.</summary>
public sealed class UnsupportedServiceManager : IServiceManager
{
    private const string Message = "service control is not implemented on this platform";
    public Task InstallAsync(ServiceSpec spec, CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task UninstallAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task StartAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task StopAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
    public Task<ServiceStatus> StatusAsync(CancellationToken ct) => throw new PlatformNotSupportedException(Message);
}
