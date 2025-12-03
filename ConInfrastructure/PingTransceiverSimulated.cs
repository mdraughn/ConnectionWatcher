using System.Net;
using ConCore;

namespace ConInfrastructure
{
    /// <summary>
    /// Simulated ping transceiver that mimics network behavior without sending actual ICMP packets.
    /// Useful for testing, demonstrations, and development without requiring network access or elevated privileges.
    /// </summary>
    public class PingTransceiverSimulated : IPingTransceiver
    {
        private readonly IPAddress _ipAddress;
        private readonly CancellationToken _cancellationToken;
        private readonly PingSimulationOptions _options;
        private readonly Random _random;
        private readonly object _lock = new();
        
        private DateTime? _outageStartTime;
        private TimeSpan _currentOutageDuration;
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the PingTransceiverSimulated class.
        /// </summary>
        /// <param name="ipAddress">The IP address to simulate pinging.</param>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        /// <param name="options">Simulation options controlling network behavior.</param>
        public PingTransceiverSimulated(IPAddress ipAddress, CancellationToken cancellationToken, PingSimulationOptions? options = null)
        {
            _ipAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            _cancellationToken = cancellationToken;
            _options = options?.Clone() ?? new PingSimulationOptions();
            _options.Validate();
            
            // Initialize random with seed if provided, otherwise use time-based seed
            _random = _options.RandomSeed.HasValue 
                ? new Random(_options.RandomSeed.Value) 
                : new Random();
        }

        /// <summary>
        /// Gets the IP address that this ping transceiver is configured to ping.
        /// </summary>
        public IPAddress IpAddress => _ipAddress;

        /// <summary>
        /// Simulates an ICMP echo request to the configured IP address.
        /// </summary>
        /// <param name="timeoutMs">Timeout in milliseconds.</param>
        /// <returns>A simulated ping result.</returns>
        public async Task<PingResult> PingAsync(int timeoutMs)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PingTransceiverSimulated));

            _cancellationToken.ThrowIfCancellationRequested();

            // Check if we should start a new outage
            CheckAndStartOutage();

            // Check if we're currently in an outage
            var isInOutage = IsCurrentlyInOutage();
            var effectivePacketLossRate = isInOutage 
                ? _options.OutagePacketLossRate 
                : _options.BasePacketLossRate;

            // Determine if this packet is lost
            var isPacketLost = _random.NextDouble() < effectivePacketLossRate;

            if (isPacketLost)
            {
                // Simulate timeout delay
                await Task.Delay(timeoutMs, _cancellationToken);
                
                var message = isInOutage 
                    ? "Request timed out (outage)" 
                    : "Request timed out";
                
                return new PingResult(_ipAddress, false, 0, message);
            }

            // Generate simulated round-trip time using Box-Muller transform for normal distribution
            var roundTripTime = GenerateRoundTripTime();

            // Simulate the network delay
            await Task.Delay((int)roundTripTime, _cancellationToken);

            return new PingResult(_ipAddress, true, (long)roundTripTime, "Success");
        }

        /// <summary>
        /// Checks if a new outage should start based on probability.
        /// </summary>
        private void CheckAndStartOutage()
        {
            lock (_lock)
            {
                // Don't start a new outage if we're already in one
                if (_outageStartTime.HasValue)
                    return;

                // Check if we should start an outage
                if (_random.NextDouble() < _options.OutageProbability)
                {
                    _outageStartTime = DateTime.UtcNow;
                    _currentOutageDuration = GenerateOutageDuration();
                }
            }
        }

        /// <summary>
        /// Checks if we're currently experiencing an outage.
        /// </summary>
        private bool IsCurrentlyInOutage()
        {
            lock (_lock)
            {
                if (!_outageStartTime.HasValue)
                    return false;

                var outageElapsed = DateTime.UtcNow - _outageStartTime.Value;
                
                if (outageElapsed >= _currentOutageDuration)
                {
                    // Outage has ended
                    _outageStartTime = null;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Generates a simulated round-trip time using a normal distribution.
        /// </summary>
        private double GenerateRoundTripTime()
        {
            // Box-Muller transform to generate normal distribution from uniform random
            var u1 = 1.0 - _random.NextDouble(); // Uniform(0,1] - avoid zero
            var u2 = 1.0 - _random.NextDouble();
            var standardNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            
            // Transform to desired mean and standard deviation
            var value = _options.MeanRoundTripTimeMs + _options.RoundTripTimeStdDevMs * standardNormal;
            
            // Clamp to reasonable bounds
            return Math.Clamp(value, _options.MinRoundTripTimeMs, _options.MaxRoundTripTimeMs);
        }

        /// <summary>
        /// Generates an outage duration using a normal distribution.
        /// </summary>
        private TimeSpan GenerateOutageDuration()
        {
            // Box-Muller transform for normal distribution
            var u1 = 1.0 - _random.NextDouble();
            var u2 = 1.0 - _random.NextDouble();
            var standardNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            
            // Transform to desired mean and standard deviation
            var durationMs = _options.MeanOutageDurationMs + _options.OutageDurationStdDevMs * standardNormal;
            
            // Clamp to reasonable bounds
            durationMs = Math.Clamp(durationMs, _options.MinOutageDurationMs, _options.MaxOutageDurationMs);
            
            return TimeSpan.FromMilliseconds(durationMs);
        }

        /// <summary>
        /// Gets the current simulation state for debugging/monitoring.
        /// </summary>
        public SimulationState GetSimulationState()
        {
            lock (_lock)
            {
                return new SimulationState
                {
                    IsInOutage = _outageStartTime.HasValue,
                    OutageStartTime = _outageStartTime,
                    OutageDuration = _outageStartTime.HasValue ? _currentOutageDuration : null,
                    OutageRemainingTime = _outageStartTime.HasValue 
                        ? _currentOutageDuration - (DateTime.UtcNow - _outageStartTime.Value)
                        : null
                };
            }
        }

        /// <summary>
        /// Represents the current state of the simulation.
        /// </summary>
        public record SimulationState
        {
            public bool IsInOutage { get; init; }
            public DateTime? OutageStartTime { get; init; }
            public TimeSpan? OutageDuration { get; init; }
            public TimeSpan? OutageRemainingTime { get; init; }
        }

        /// <summary>
        /// Releases all resources used by the PingTransceiverSimulated.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources and optionally releases the managed resources.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // No managed resources to dispose for simulated transceiver
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer for PingTransceiverSimulated.
        /// </summary>
        ~PingTransceiverSimulated()
        {
            Dispose(false);
        }
    }
}