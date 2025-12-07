using System.Windows;

namespace ConInfrastructure.Views
{
    /// <summary>
    /// Modal dialog for editing ping simulation settings.
    /// </summary>
    public partial class SimulationSettingsDialog : Window
    {
        private readonly PingSimulationOptions _options;

        public SimulationSettingsDialog(PingSimulationOptions options)
        {
            InitializeComponent();
            _options = options;
            DataContext = options;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Update the options from the text boxes
                _options.MeanRoundTripTimeMs = double.Parse(MeanRttTextBox.Text);
                _options.RoundTripTimeStdDevMs = double.Parse(RttStdDevTextBox.Text);
                _options.MinRoundTripTimeMs = double.Parse(MinRttTextBox.Text);
                _options.MaxRoundTripTimeMs = double.Parse(MaxRttTextBox.Text);
                _options.BasePacketLossRate = double.Parse(BasePacketLossTextBox.Text);
                _options.OutageProbability = double.Parse(OutageProbabilityTextBox.Text);
                _options.MeanOutageDurationMs = double.Parse(MeanOutageDurationTextBox.Text);
                _options.OutageDurationStdDevMs = double.Parse(OutageDurationStdDevTextBox.Text);
                _options.MinOutageDurationMs = double.Parse(MinOutageDurationTextBox.Text);
                _options.MaxOutageDurationMs = double.Parse(MaxOutageDurationTextBox.Text);
                _options.OutagePacketLossRate = double.Parse(OutagePacketLossTextBox.Text);
                
                // Handle random seed (nullable)
                if (string.IsNullOrWhiteSpace(RandomSeedTextBox.Text))
                {
                    _options.RandomSeed = null;
                }
                else
                {
                    _options.RandomSeed = int.Parse(RandomSeedTextBox.Text);
                }

                // Validate the options
                _options.Validate();

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Invalid settings: {ex.Message}", "Validation Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
