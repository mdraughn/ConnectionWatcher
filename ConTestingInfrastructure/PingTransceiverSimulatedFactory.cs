using System.Net;
using ConCore;

namespace ConInfrastructure
{
    /// <summary>
    /// Factory for creating simulated ping transceivers with configurable network behavior.
    /// Useful for testing, demonstrations, and development without actual network access.
    /// </summary>
    public class PingTransceiverSimulatedFactory : IPingTransceiverFactory
    {
        private readonly PingSimulationOptions _options;

        /// <summary>
        /// Initializes a new instance of the PingTransceiverSimulatedFactory class with default options.
        /// </summary>
        public PingTransceiverSimulatedFactory()
            : this(new PingSimulationOptions())
        {
        }

        /// <summary>
        /// Initializes a new instance of the PingTransceiverSimulatedFactory class with custom options.
        /// </summary>
        /// <param name="options">Simulation options controlling network behavior.</param>
        public PingTransceiverSimulatedFactory(PingSimulationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();
        }

        /// <summary>
        /// Creates a simulated ping transceiver for the specified IP address.
        /// </summary>
        /// <param name="ipAddress">The IP address to simulate pinging.</param>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        /// <returns>A simulated ping transceiver instance.</returns>
        public IPingTransceiver Create(IPAddress ipAddress, CancellationToken cancellationToken)
        {
            return new PingTransceiverSimulated(ipAddress, cancellationToken, _options);
        }

        /// <summary>
        /// Gets the simulation options used by this factory.
        /// </summary>
        public PingSimulationOptions Options => _options;

        /// <summary>
        /// Creates a factory configured for a perfect network (no packet loss, consistent latency).
        /// </summary>
        /// <param name="latencyMs">The consistent latency in milliseconds.</param>
        public static PingTransceiverSimulatedFactory CreatePerfectNetwork(double latencyMs = 10.0)
        {
            return new PingTransceiverSimulatedFactory(
                PingSimulationOptions.CreatePerfectNetwork(latencyMs));
        }

        /// <summary>
        /// Creates a factory configured for a typical stable network.
        /// </summary>
        public static PingTransceiverSimulatedFactory CreateStableNetwork()
        {
            return new PingTransceiverSimulatedFactory(
                PingSimulationOptions.CreateStableNetwork());
        }

        /// <summary>
        /// Creates a factory configured for an unreliable network with frequent issues.
        /// </summary>
        public static PingTransceiverSimulatedFactory CreateUnreliableNetwork()
        {
            return new PingTransceiverSimulatedFactory(
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
            public PingTransceiverSimulatedFactory Build()
            {
                return new PingTransceiverSimulatedFactory(_options);
            }
        }
    }
}