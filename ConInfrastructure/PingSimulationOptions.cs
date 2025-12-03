namespace ConInfrastructure
{
    /// <summary>
    /// Options for configuring simulated ping behavior.
    /// </summary>
    public class PingSimulationOptions
    {
        /// <summary>
        /// Gets or sets the mean round-trip time in milliseconds.
        /// </summary>
        public double MeanRoundTripTimeMs { get; set; } = 20.0;

        /// <summary>
        /// Gets or sets the standard deviation for round-trip time in milliseconds.
        /// </summary>
        public double RoundTripTimeStdDevMs { get; set; } = 5.0;

        /// <summary>
        /// Gets or sets the base packet loss rate as a ratio between 0.0 and 1.0.
        /// 0.0 = no packet loss, 1.0 = 100% packet loss.
        /// </summary>
        public double BasePacketLossRate { get; set; } = 0.01;

        /// <summary>
        /// Gets or sets the probability of an outage occurring per ping operation.
        /// Value between 0.0 and 1.0. Default is 0.001 (0.1% chance per ping).
        /// </summary>
        public double OutageProbability { get; set; } = 0.001;

        /// <summary>
        /// Gets or sets the mean duration of an outage in milliseconds.
        /// </summary>
        public double MeanOutageDurationMs { get; set; } = 30000.0; // 30 seconds

        /// <summary>
        /// Gets or sets the standard deviation for outage duration in milliseconds.
        /// </summary>
        public double OutageDurationStdDevMs { get; set; } = 10000.0; // 10 seconds

        /// <summary>
        /// Gets or sets the packet loss rate during an outage.
        /// Value between 0.0 and 1.0. Default is 1.0 (100% loss during outage).
        /// </summary>
        public double OutagePacketLossRate { get; set; } = 1.0;

        /// <summary>
        /// Gets or sets the minimum round-trip time in milliseconds (prevents negative values).
        /// </summary>
        public double MinRoundTripTimeMs { get; set; } = 1.0;

        /// <summary>
        /// Gets or sets the maximum round-trip time in milliseconds (caps extreme values).
        /// </summary>
        public double MaxRoundTripTimeMs { get; set; } = 500.0;

        /// <summary>
        /// Gets or sets the minimum outage duration in milliseconds.
        /// </summary>
        public double MinOutageDurationMs { get; set; } = 1000.0; // 1 second

        /// <summary>
        /// Gets or sets the maximum outage duration in milliseconds.
        /// </summary>
        public double MaxOutageDurationMs { get; set; } = 120000.0; // 2 minutes

        /// <summary>
        /// Gets or sets the random seed for reproducible simulations.
        /// If null, a random seed will be used.
        /// </summary>
        public int? RandomSeed { get; set; }

        /// <summary>
        /// Validates the options and throws if any values are invalid.
        /// </summary>
        public void Validate()
        {
            if (MeanRoundTripTimeMs < 0)
                throw new ArgumentException("Mean round-trip time cannot be negative", nameof(MeanRoundTripTimeMs));

            if (RoundTripTimeStdDevMs < 0)
                throw new ArgumentException("Round-trip time standard deviation cannot be negative", nameof(RoundTripTimeStdDevMs));

            if (BasePacketLossRate is < 0.0 or > 1.0)
                throw new ArgumentException("Base packet loss rate must be between 0.0 and 1.0", nameof(BasePacketLossRate));

            if (OutageProbability is < 0.0 or > 1.0)
                throw new ArgumentException("Outage probability must be between 0.0 and 1.0", nameof(OutageProbability));

            if (OutagePacketLossRate is < 0.0 or > 1.0)
                throw new ArgumentException("Outage packet loss rate must be between 0.0 and 1.0", nameof(OutagePacketLossRate));

            if (MeanOutageDurationMs < 0)
                throw new ArgumentException("Mean outage duration cannot be negative", nameof(MeanOutageDurationMs));

            if (OutageDurationStdDevMs < 0)
                throw new ArgumentException("Outage duration standard deviation cannot be negative", nameof(OutageDurationStdDevMs));

            if (MinRoundTripTimeMs < 0)
                throw new ArgumentException("Minimum round-trip time cannot be negative", nameof(MinRoundTripTimeMs));

            if (MaxRoundTripTimeMs < MinRoundTripTimeMs)
                throw new ArgumentException("Maximum round-trip time must be greater than minimum", nameof(MaxRoundTripTimeMs));

            if (MinOutageDurationMs < 0)
                throw new ArgumentException("Minimum outage duration cannot be negative", nameof(MinOutageDurationMs));

            if (MaxOutageDurationMs < MinOutageDurationMs)
                throw new ArgumentException("Maximum outage duration must be greater than minimum", nameof(MaxOutageDurationMs));
        }

        /// <summary>
        /// Creates options for a perfect network (no packet loss, consistent latency).
        /// </summary>
        public static PingSimulationOptions CreatePerfectNetwork(double latencyMs = 10.0)
        {
            return new PingSimulationOptions
            {
                MeanRoundTripTimeMs = latencyMs,
                RoundTripTimeStdDevMs = 0.5,
                BasePacketLossRate = 0.0,
                OutageProbability = 0.0,
                OutagePacketLossRate = 0.0
            };
        }

        /// <summary>
        /// Creates options for a typical stable network with low latency.
        /// </summary>
        public static PingSimulationOptions CreateStableNetwork()
        {
            return new PingSimulationOptions
            {
                MeanRoundTripTimeMs = 20.0,
                RoundTripTimeStdDevMs = 5.0,
                BasePacketLossRate = 0.001, // 0.1% loss
                OutageProbability = 0.0001, // Very rare
                MeanOutageDurationMs = 5000.0,
                OutageDurationStdDevMs = 2000.0,
                OutagePacketLossRate = 0.8
            };
        }

        /// <summary>
        /// Creates options for an unreliable network with frequent issues.
        /// </summary>
        public static PingSimulationOptions CreateUnreliableNetwork()
        {
            return new PingSimulationOptions
            {
                MeanRoundTripTimeMs = 100.0,
                RoundTripTimeStdDevMs = 50.0,
                BasePacketLossRate = 0.05, // 5% loss
                OutageProbability = 0.01, // 1% chance per ping
                MeanOutageDurationMs = 15000.0,
                OutageDurationStdDevMs = 5000.0,
                OutagePacketLossRate = 1.0
            };
        }

        /// <summary>
        /// Creates a copy of these options.
        /// </summary>
        public PingSimulationOptions Clone()
        {
            return new PingSimulationOptions
            {
                MeanRoundTripTimeMs = MeanRoundTripTimeMs,
                RoundTripTimeStdDevMs = RoundTripTimeStdDevMs,
                BasePacketLossRate = BasePacketLossRate,
                OutageProbability = OutageProbability,
                MeanOutageDurationMs = MeanOutageDurationMs,
                OutageDurationStdDevMs = OutageDurationStdDevMs,
                OutagePacketLossRate = OutagePacketLossRate,
                MinRoundTripTimeMs = MinRoundTripTimeMs,
                MaxRoundTripTimeMs = MaxRoundTripTimeMs,
                MinOutageDurationMs = MinOutageDurationMs,
                MaxOutageDurationMs = MaxOutageDurationMs,
                RandomSeed = RandomSeed
            };
        }
    }
}