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
        private readonly SimulationControlManager _controlManager;
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the PingTransceiverInteractiveSimFactory class with custom options.
        /// </summary>
        /// <param name="options">Simulation options controlling network behavior.</param>
        public PingTransceiverInteractiveSimFactory(PingSimulationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
            
            // Initialize the UI control manager on a separate thread
            _controlManager = new SimulationControlManager();
            _controlManager.Initialize();
        }

        /// <summary>
        /// Creates an interactive simulated ping transceiver for the specified IP address.
        /// </summary>
        /// <param name="ipAddress">The IP address to simulate pinging.</param>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        /// <returns>An interactive simulated ping transceiver instance.</returns>
        public IPingTransceiver Create(IPAddress ipAddress, CancellationToken cancellationToken)
        {
            // Register this IP address with the control manager
            var viewModel = _controlManager.RegisterSimulator(ipAddress, _options);
            
            return new PingTransceiverInteractiveSim(ipAddress, cancellationToken, _options, viewModel);
        }

        /// <summary>
        /// Gets the simulation options used by this factory.
        /// </summary>
        public PingSimulationOptions Options => _options;

        /// <summary>
        /// Creates a factory configured for a perfect network (no packet loss, consistent latency).
        /// </summary>
        /// <param name="latencyMs">The consistent latency in milliseconds.</param>
        public static PingTransceiverInteractiveSimFactory CreatePerfectNetwork(double latencyMs = 10.0)
        {
            return new PingTransceiverInteractiveSimFactory(
                PingSimulationOptions.CreatePerfectNetwork(latencyMs));
        }

        /// <summary>
        /// Creates a factory configured for a typical stable network.
        /// </summary>
        public static PingTransceiverInteractiveSimFactory CreateStableNetwork()
        {
            return new PingTransceiverInteractiveSimFactory(
                PingSimulationOptions.CreateStableNetwork());
        }

        /// <summary>
        /// Creates a factory configured for an unreliable network with frequent issues.
        /// </summary>
        public static PingTransceiverInteractiveSimFactory CreateUnreliableNetwork()
        {
            return new PingTransceiverInteractiveSimFactory(
                PingSimulationOptions.CreateUnreliableNetwork());
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
            /// Builds the factory with the configured options.
            /// </summary>
            public PingTransceiverInteractiveSimFactory Build()
            {
                return new PingTransceiverInteractiveSimFactory(_options);
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