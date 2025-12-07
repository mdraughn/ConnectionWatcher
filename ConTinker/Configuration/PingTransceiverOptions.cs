using System.ComponentModel.DataAnnotations;
using ConInfrastructure;

namespace ConTinker.Configuration
{
    /// <summary>
    /// Configuration options for ping transceiver selection and behavior.
    /// </summary>
    public class PingTransceiverOptions
    {
        public const string SectionName = "PingTransceiver";

        /// <summary>
        /// Gets or sets whether to use simulated pinging instead of real ICMP pings.
        /// Default is false (use real pinging).
        /// </summary>
        public bool UseSimulation { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to show the interactive control window for simulations.
        /// Only applies when UseSimulation is true.
        /// Default is true when simulation is enabled.
        /// </summary>
        public bool ShowControlWindow { get; set; } = true;

        /// <summary>
        /// Gets or sets the simulation preset to use: "Perfect", "Stable", "Unreliable", or "Custom".
        /// Only applies when UseSimulation is true.
        /// Default is "Stable".
        /// </summary>
        [AllowedValues("Perfect", "Stable", "Unreliable", "Custom")]
        public string SimulationPreset { get; set; } = "Stable";

        /// <summary>
        /// Gets or sets custom simulation options. Only used when SimulationPreset is "Custom".
        /// </summary>
        public PingSimulationOptions? CustomSimulation { get; set; }

        /// <summary>
        /// Validates the options.
        /// </summary>
        public void Validate()
        {
            if (!UseSimulation)
            {
                // No validation needed for real ICMP pinging
                return;
            }

            var validPresets = new[] { "Perfect", "Stable", "Unreliable", "Custom" };
            if (!validPresets.Contains(SimulationPreset, StringComparer.OrdinalIgnoreCase))
            {
                throw new ValidationException(
                    $"SimulationPreset must be one of: {string.Join(", ", validPresets)}");
            }

            if (SimulationPreset.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            {
                if (CustomSimulation == null)
                {
                    throw new ValidationException(
                        "CustomSimulation must be provided when SimulationPreset is 'Custom'");
                }

                CustomSimulation.Validate();
            }
        }
    }
}
