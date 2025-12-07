using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace ConInfrastructure.ViewModels
{
    /// <summary>
    /// ViewModel representing the state of a single ping simulator for a specific IP address.
    /// </summary>
    public class PingSimulatorViewModel : INotifyPropertyChanged
    {
        private bool _isOutageActive;
        private int _totalPings;
        private string _lastResult = "No pings yet";
        private readonly PingSimulationOptions _options;

        public PingSimulatorViewModel(IPAddress ipAddress, PingSimulationOptions options)
        {
            IpAddress = ipAddress;
            _options = options.Clone();
        }

        /// <summary>
        /// Gets the IP address being simulated.
        /// </summary>
        public IPAddress IpAddress { get; }

        /// <summary>
        /// Gets or sets whether an outage is currently active (user-controlled).
        /// </summary>
        public bool IsOutageActive
        {
            get => _isOutageActive;
            set
            {
                if (_isOutageActive != value)
                {
                    _isOutageActive = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Gets the total number of pings received.
        /// </summary>
        public int TotalPings
        {
            get => _totalPings;
            private set
            {
                if (_totalPings != value)
                {
                    _totalPings = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Gets the last ping result as a display string.
        /// </summary>
        public string LastResult
        {
            get => _lastResult;
            private set
            {
                if (_lastResult != value)
                {
                    _lastResult = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Gets the simulation options for this IP address.
        /// </summary>
        public PingSimulationOptions Options => _options;

        /// <summary>
        /// Updates the statistics when a ping is sent.
        /// </summary>
        public void RecordPing(bool success, long roundtripTime)
        {
            TotalPings++;
            LastResult = success 
                ? $"Success: {roundtripTime}ms" 
                : "Failed";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
