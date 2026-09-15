using System.Net;
using ConCore;

namespace ConInfrastructure
{
    /// <summary>
    /// Factory for creating interactive simulated ping transceivers with UI control.
    /// </summary>
    public class PingTransceiverInteractiveSimFactory : IPingTransceiverFactory, IDisposable
    {
        private readonly PingSimulationOptions _options;
        private readonly SimulationControlManager? _controlManager;
        private readonly bool _showControlWindow;
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the PingTransceiverInteractiveSimFactory class with custom options.
        /// </summary>
        /// <param name="options">Simulation options controlling network behavior.</param>
        /// <param name="showControlWindow">Whether to display the interactive control window. Defaults to false.</param>
        public PingTransceiverInteractiveSimFactory(PingSimulationOptions options, bool showControlWindow = false)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            _showControlWindow = showControlWindow;
            
            // Initialize the UI control manager on a separate thread only if requested
            if (_showControlWindow)
            {
                _controlManager = new SimulationControlManager();
                _controlManager.Initialize();
            }
        }

        /// <summary>
        /// Creates an interactive simulated ping transceiver for the specified IP address.
        /// </summary>
        /// <param name="ipAddress">The IP address to simulate pinging.</param>
        /// <param name="probe">The probe spec (currently unused by the simulation).</param>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        /// <returns>An interactive simulated ping transceiver instance.</returns>
        public IPingTransceiver Create(IPAddress ipAddress, ProbeSpec probe, CancellationToken cancellationToken)
        {
            // Register this IP address with the control manager only if window is shown
            var viewModel = _showControlWindow && _controlManager != null
                ? _controlManager.RegisterSimulator(ipAddress, _options)
                : null;
            
            return new PingTransceiverInteractiveSim(ipAddress, cancellationToken, _options, viewModel);
        }

        /// <summary>
        /// Gets the simulation options used by this factory.
        /// </summary>
        public PingSimulationOptions Options => _options;

        /// <summary>
        /// Gets whether the control window is shown.
        /// </summary>
        public bool ShowControlWindow => _showControlWindow;

        /// <summary>
        /// Creates a factory configured for a perfect network (no packet loss, consistent latency).
        /// </summary>
        /// <param name="latencyMs">The consistent latency in milliseconds.</param>
        /// <param name="showControlWindow">Whether to display the interactive control window. Defaults to false.</param>
        public static PingTransceiverInteractiveSimFactory CreatePerfectNetwork(double latencyMs = 10.0, bool showControlWindow = false)
        {
            return new PingTransceiverInteractiveSimFactory(
                PingSimulationOptions.CreatePerfectNetwork(latencyMs), showControlWindow);
        }

        /// <summary>
        /// Creates a factory configured for a typical stable network.
        /// </summary>
        /// <param name="showControlWindow">Whether to display the interactive control window. Defaults to false.</param>
        public static PingTransceiverInteractiveSimFactory CreateStableNetwork(bool showControlWindow = false)
        {
            return new PingTransceiverInteractiveSimFactory(
                PingSimulationOptions.CreateStableNetwork(), showControlWindow);
        }

        /// <summary>
        /// Creates a factory configured for an unreliable network with frequent issues.
        /// </summary>
        /// <param name="showControlWindow">Whether to display the interactive control window. Defaults to false.</param>
        public static PingTransceiverInteractiveSimFactory CreateUnreliableNetwork(bool showControlWindow = false)
        {
            return new PingTransceiverInteractiveSimFactory(
                PingSimulationOptions.CreateUnreliableNetwork(), showControlWindow);
        }

        /// <summary>
        /// Creates a factory with custom configuration using a builder pattern.
        /// </summary>
        public static Builder CreateCustom()
        {
            return new Builder();
        }

        /// <summary>
        /// Builder for creating customized ping simulation factories.
        /// </summary>
        public class Builder
        {
            private readonly PingSimulationOptions _options = new();
            private bool _showControlWindow = false;

            /// <summary>
            /// Sets the mean round-trip time and standard deviation.
            /// </summary>
            public Builder WithLatency(double meanMs, double stdDevMs)
            {
                _options.MeanRoundTripTimeMs = meanMs;
                _options.RoundTripTimeStdDevMs = stdDevMs;
                return this;
            }

            /// <summary>
            /// Sets the base packet loss rate.
            /// </summary>
            public Builder WithPacketLoss(double lossRate)
            {
                _options.BasePacketLossRate = lossRate;
                return this;
            }

            /// <summary>
            /// Sets the outage probability and duration.
            /// </summary>
            public Builder WithOutages(double probability, double meanDurationMs, double stdDevMs)
            {
                _options.OutageProbability = probability;
                _options.MeanOutageDurationMs = meanDurationMs;
                _options.OutageDurationStdDevMs = stdDevMs;
                return this;
            }

            /// <summary>
            /// Sets the packet loss rate during outages.
            /// </summary>
            public Builder WithOutagePacketLoss(double lossRate)
            {
                _options.OutagePacketLossRate = lossRate;
                return this;
            }

            /// <summary>
            /// Sets a random seed for reproducible simulations.
            /// </summary>
            public Builder WithRandomSeed(int seed)
            {
                _options.RandomSeed = seed;
                return this;
            }

            /// <summary>
            /// Sets the bounds for round-trip time.
            /// </summary>
            public Builder WithLatencyBounds(double minMs, double maxMs)
            {
                _options.MinRoundTripTimeMs = minMs;
                _options.MaxRoundTripTimeMs = maxMs;
                return this;
            }

            /// <summary>
            /// Sets the bounds for outage duration.
            /// </summary>
            public Builder WithOutageDurationBounds(double minMs, double maxMs)
            {
                _options.MinOutageDurationMs = minMs;
                _options.MaxOutageDurationMs = maxMs;
                return this;
            }

            /// <summary>
            /// Enables or disables the interactive control window.
            /// </summary>
            /// <param name="show">Whether to display the interactive control window. Defaults to true.</param>
            public Builder WithControlWindow(bool show = true)
            {
                _showControlWindow = show;
                return this;
            }

            /// <summary>
            /// Builds the factory with the configured options.
            /// </summary>
            public PingTransceiverInteractiveSimFactory Build()
            {
                return new PingTransceiverInteractiveSimFactory(_options, _showControlWindow);
            }
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
                    _controlManager?.Dispose();
                }
                _disposed = true;
            }
        }
    }
}