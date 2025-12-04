using Microsoft.Extensions.Options;

namespace ConEngine.Configuration
{
    /// <summary>
    /// Validator for HostTrackingOptions using IValidateOptions pattern.
    /// </summary>
    public class HostTrackingOptionsValidator : IValidateOptions<HostTrackingOptions>
    {
        public ValidateOptionsResult Validate(string? name, HostTrackingOptions options)
        {
            if (options == null)
            {
                return ValidateOptionsResult.Fail("HostTrackingOptions cannot be null.");
            }

            var validationErrors = new List<string>();

            // Validate HistorySize range
            if (options.HistorySize < 1 || options.HistorySize > 1000)
            {
                validationErrors.Add($"HistorySize must be between 1 and 1000, but was {options.HistorySize}.");
            }

            // Validate MinUsableSample range
            if (options.MinUsableSample < 1)
            {
                validationErrors.Add($"MinUsableSample must be at least 1, but was {options.MinUsableSample}.");
            }

            // Validate relationship between MinUsableSample and HistorySize
            if (options.MinUsableSample > options.HistorySize)
            {
                validationErrors.Add(
                    $"MinUsableSample ({options.MinUsableSample}) cannot be greater than HistorySize ({options.HistorySize}).");
            }

            if (validationErrors.Count > 0)
            {
                return ValidateOptionsResult.Fail(string.Join(" ", validationErrors));
            }

            return ValidateOptionsResult.Success;
        }
    }
}