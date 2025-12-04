using System.ComponentModel.DataAnnotations;

namespace ConEngine.Configuration
{
    /// <summary>
    /// Configuration options for host tracking behavior.
    /// </summary>
    public class HostTrackingOptions
    {
        /// <summary>
        /// Configuration section name in appsettings.json
        /// </summary>
        public const string SectionName = "HostTracking";

        /// <summary>
        /// Gets or sets the size of the history window for each tracked host.
        /// This determines how many recent ping results are kept in memory.
        /// </summary>
        [Range(1, 1000, ErrorMessage = "HistorySize must be between 1 and 1000.")]
        public int HistorySize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the minimum number of samples required for statistics to be considered usable.
        /// Must be between 1 and HistorySize inclusive.
        /// </summary>
        [Range(1, int.MaxValue, ErrorMessage = "MinUsableSample must be at least 1.")]
        public int MinUsableSample { get; set; } = 10;

        /// <summary>
        /// Validates that MinUsableSample is not greater than HistorySize.
        /// </summary>
        /// <param name="validationContext">The validation context.</param>
        /// <returns>Validation result.</returns>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (MinUsableSample > HistorySize)
            {
                yield return new ValidationResult(
                    $"MinUsableSample ({MinUsableSample}) cannot be greater than HistorySize ({HistorySize}).",
                    new[] { nameof(MinUsableSample) });
            }

            if (MinUsableSample < 1)
            {
                yield return new ValidationResult(
                    $"MinUsableSample must be at least 1.",
                    new[] { nameof(MinUsableSample) });
            }
        }
    }
}