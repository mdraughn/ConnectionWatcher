using System.Windows;
using System.Windows.Controls;
using ConInfrastructure.ViewModels;

namespace ConInfrastructure.Views
{
    /// <summary>
    /// Control for managing a single ping simulator.
    /// </summary>
    public partial class PingSimulatorControl : UserControl
    {
        public PingSimulatorControl()
        {
            InitializeComponent();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is PingSimulatorViewModel viewModel)
            {
                var settingsDialog = new SimulationSettingsDialog(viewModel.Options)
                {
                    Owner = Window.GetWindow(this)
                };
                settingsDialog.ShowDialog();
            }
        }
    }
}
