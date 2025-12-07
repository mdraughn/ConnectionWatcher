using ConCore;
using ConInfrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConTinker.Configuration
{
    /// <summary>
    /// Factory that creates the appropriate IPingTransceiverFactory based on configuration.
    /// </summary>
    public class ConfiguredPingTransceiverFactory : IPingTransceiverFactory, IDisposable
    {
        private readonly IPingTransceiverFactory _innerFactory;
        private readonly ILogger<ConfiguredPingTransceiverFactory> _logger;
        private bool _disposed = false;

        public ConfiguredPingTransceiverFactory(
            IOptions<PingTransceiverOptions> options,
            ILogger<ConfiguredPingTransceiverFactory> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            var config = options?.Value ?? throw new ArgumentNullException(nameof(options));

            if (!config.UseSimulation)
            {
                _logger.LogInformation("Using real ICMP ping transceiver");
                _innerFactory = new PingTransceiverFactoryIcmp();
            }
            else
            {
                _logger.LogInformation(
                    "Using simulated ping transceiver - Preset: {Preset}, ControlWindow: {ControlWindow}",
                    config.SimulationPreset,
                    config.ShowControlWindow);

                _innerFactory = CreateSimulationFactory(config);
            }
        }

        private static PingTransceiverInteractiveSimFactory CreateSimulationFactory(PingTransceiverOptions config)
        {
            return config.SimulationPreset.ToLowerInvariant() switch
            {
                "perfect" => PingTransceiverInteractiveSimFactory.CreatePerfectNetwork(
                    showControlWindow: config.ShowControlWindow),
                
                "stable" => PingTransceiverInteractiveSimFactory.CreateStableNetwork(
                    showControlWindow: config.ShowControlWindow),
                
                "unreliable" => PingTransceiverInteractiveSimFactory.CreateUnreliableNetwork(
                    showControlWindow: config.ShowControlWindow),
                
                "custom" => new PingTransceiverInteractiveSimFactory(
                    config.CustomSimulation ?? throw new InvalidOperationException("CustomSimulation is required"),
                    config.ShowControlWindow),
                
                _ => throw new InvalidOperationException($"Unknown simulation preset: {config.SimulationPreset}")
            };
        }

        public IPingTransceiver Create(System.Net.IPAddress ipAddress, CancellationToken cancellationToken)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConfiguredPingTransceiverFactory));

            return _innerFactory.Create(ipAddress, cancellationToken);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    if (_innerFactory is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
                _disposed = true;
            }
        }
    }
}
