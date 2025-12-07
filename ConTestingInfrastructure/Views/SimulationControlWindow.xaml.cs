using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using ConInfrastructure.ViewModels;

namespace ConInfrastructure.Views
{
    /// <summary>
    /// Main control window for managing ping simulations.
    /// </summary>
    public partial class SimulationControlWindow : Window
    {
        public ObservableCollection<PingSimulatorViewModel> Simulators { get; }

        public SimulationControlWindow()
        {
            InitializeComponent();
            Simulators = new ObservableCollection<PingSimulatorViewModel>();
            SimulatorsItemsControl.ItemsSource = Simulators;
        }

        /// <summary>
        /// Adds a new simulator to the list.
        /// </summary>
        public void AddSimulator(PingSimulatorViewModel viewModel)
        {
            if (Dispatcher.CheckAccess())
            {
                Simulators.Add(viewModel);
            }
            else
            {
                Dispatcher.Invoke(() => Simulators.Add(viewModel));
            }
        }

        /// <summary>
        /// Gets a simulator by IP address.
        /// </summary>
        public PingSimulatorViewModel? GetSimulator(System.Net.IPAddress ipAddress)
        {
            if (Dispatcher.CheckAccess())
            {
                return Simulators.FirstOrDefault(s => s.IpAddress.Equals(ipAddress));
            }
            else
            {
                return Dispatcher.Invoke(() => Simulators.FirstOrDefault(s => s.IpAddress.Equals(ipAddress)));
            }
        }
    }
}
