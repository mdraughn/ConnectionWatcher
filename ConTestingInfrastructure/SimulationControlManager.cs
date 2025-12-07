using System.Net;
using System.Windows;
using System.Windows.Threading;
using ConInfrastructure.ViewModels;
using ConInfrastructure.Views;

namespace ConInfrastructure
{
    /// <summary>
    /// Manages the interactive simulation control window on a separate UI thread.
    /// </summary>
    public class SimulationControlManager : IDisposable
    {
        private SimulationControlWindow? _window;
        private Thread? _uiThread;
        private readonly ManualResetEventSlim _windowReady = new(false);
        private Dispatcher? _dispatcher;
        private bool _disposed = false;

        /// <summary>
        /// Initializes and starts the simulation control window on a separate thread.
        /// </summary>
        public void Initialize()
        {
            if (_uiThread != null)
                return;

            _uiThread = new Thread(() =>
            {
                // Create a new WPF application context for this thread
                _window = new SimulationControlWindow();
                _dispatcher = _window.Dispatcher;

                _window.Closed += (s, e) =>
                {
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                };

                _windowReady.Set();
                _window.Show();

                // Start the WPF message pump
                Dispatcher.Run();
            });

            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;
            _uiThread.Start();

            // Wait for the window to be created
            _windowReady.Wait();
        }

        /// <summary>
        /// Registers a new ping simulator for tracking.
        /// </summary>
        public PingSimulatorViewModel RegisterSimulator(IPAddress ipAddress, PingSimulationOptions options)
        {
            if (_dispatcher == null || _window == null)
                throw new InvalidOperationException("SimulationControlManager not initialized");

            PingSimulatorViewModel? viewModel = null;

            _dispatcher.Invoke(() =>
            {
                // Check if already registered
                viewModel = _window.GetSimulator(ipAddress);
                if (viewModel == null)
                {
                    viewModel = new PingSimulatorViewModel(ipAddress, options);
                    _window.AddSimulator(viewModel);
                }
            });

            return viewModel!;
        }

        /// <summary>
        /// Gets a simulator view model by IP address.
        /// </summary>
        public PingSimulatorViewModel? GetSimulator(IPAddress ipAddress)
        {
            if (_dispatcher == null || _window == null)
                return null;

            return _dispatcher.Invoke(() => _window.GetSimulator(ipAddress));
        }

        /// <summary>
        /// Shuts down the simulation control window.
        /// </summary>
        public void Shutdown()
        {
            if (_dispatcher != null)
            {
                _dispatcher.InvokeShutdown();
            }

            if (_uiThread != null && _uiThread.IsAlive)
            {
                _uiThread.Join(TimeSpan.FromSeconds(5));
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
                    Shutdown();
                    _windowReady.Dispose();
                }
                _disposed = true;
            }
        }
    }
}
