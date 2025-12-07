using Microsoft.Extensions.Options;

namespace ConTinker.Configuration
{
    /// <summary>
    /// Validates PingTransceiverOptions.
    /// </summary>
    public class PingTransceiverOptionsValidator : IValidateOptions<PingTransceiverOptions>
    {
        public ValidateOptionsResult Validate(string? name, PingTransceiverOptions options)
        {
            try
            {
                options.Validate();
                return ValidateOptionsResult.Success;
            }
            catch (Exception ex)
            {
                return ValidateOptionsResult.Fail(ex.Message);
            }
        }
    }
}
