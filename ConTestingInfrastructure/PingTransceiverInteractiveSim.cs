using System.Net;
using ConCore;
using ConInfrastructure.ViewModels;

namespace ConInfrastructure
{
    /// <summary>
    /// Interactive simulated ping transceiver that can be controlled via UI.
    /// </summary>
    public class PingTransceiverInteractiveSim : IPingTransceiver
    {
        private readonly IPAddress _ipAddress;
        private readonly CancellationToken _cancellationToken;
        private readonly PingSimulationOptions _options;
        private readonly Random _random;
        private readonly Lock _lock = new();
        private readonly PingSimulatorViewModel? _viewModel;

        private DateTime? _outageStartTime;
        private TimeSpan _currentOutageDuration;
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the PingTransceiverInteractiveSim class.
        /// </summary>
        /// <param name="ipAddress">The IP address to simulate pinging.</param>
        /// <param name="cancellationToken">Cancellation token for async operations.</param>
        /// <param name="options">Simulation options controlling network behavior.</param>
        /// <param name="viewModel">Optional view model for UI integration.</param>
        public PingTransceiverInteractiveSim(IPAddress ipAddress, CancellationToken cancellationToken, 
            PingSimulationOptions? options = null, PingSimulatorViewModel? viewModel = null)
        {
            _ipAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
            _cancellationToken = cancellationToken;
            _options = options?.Clone() ?? new PingSimulationOptions();
            _options.Validate();
            _viewModel = viewModel;

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
            if(_disposed)
                throw new ObjectDisposedException(nameof(PingTransceiverInteractiveSim));

            _cancellationToken.ThrowIfCancellationRequested();

            // Check if user has activated manual outage from UI
            bool isManualOutage = _viewModel?.IsOutageActive ?? false;

            // Check if we should start a new automatic outage
            if (!isManualOutage)
            {
                CheckAndStartOutage();
            }

            // Check if we're currently in an automatic outage or manual outage
            var isInOutage = isManualOutage || IsCurrentlyInOutage();
            var effectivePacketLossRate = isInOutage
                ? _options.OutagePacketLossRate
                : _options.BasePacketLossRate;

            // Determine if this packet is lost
            var isPacketLost = _random.NextDouble() < effectivePacketLossRate;

            if(isPacketLost)
            {
                // Simulate timeout delay
                await Task.Delay(timeoutMs, _cancellationToken);

                var message = isInOutage
                    ? "Request timed out (outage)"
                    : "Request timed out";

                var result = new PingResult(_ipAddress, false, 0, message);
                
                // Update UI
                _viewModel?.RecordPing(false, 0);
                
                return result;
            }

            // Generate simulated round-trip time using Box-Muller transform for normal distribution
            var roundTripTime = GenerateRoundTripTime();

            // Simulate the network delay
            await Task.Delay((int)roundTripTime, _cancellationToken);

            var successResult = new PingResult(_ipAddress, true, (long)roundTripTime, "Success");
            
            // Update UI
            _viewModel?.RecordPing(true, (long)roundTripTime);
            
            return successResult;
        }

        /// <summary>
        /// Checks if a new outage should start based on probability.
        /// </summary>
        private void CheckAndStartOutage()
        {
            lock(_lock)
            {
                // Don't start a new outage if we're already in one
                if(_outageStartTime.HasValue)
                    return;

                // Check if we should start an outage
                if(_random.NextDouble() < _options.OutageProbability)
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
            lock(_lock)
            {
                if(!_outageStartTime.HasValue)
                    return false;

                var outageElapsed = DateTime.UtcNow - _outageStartTime.Value;

                if(outageElapsed >= _currentOutageDuration)
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
            lock(_lock)
            {
                return new SimulationState {
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
        /// Releases all resources used by the PingTransceiverInteractiveSim.
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
            if(!_disposed)
            {
                if(disposing)
                {
                    // No managed resources to dispose for simulated transceiver
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Finalizer for PingTransceiverInteractiveSim.
        /// </summary>
        ~PingTransceiverInteractiveSim()
        {
            Dispose(false);
        }
    }
}