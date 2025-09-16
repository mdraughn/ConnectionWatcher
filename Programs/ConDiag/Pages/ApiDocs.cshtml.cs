using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConDiag.Pages;

/// <summary>
/// Page model for the API documentation page.
/// </summary>
public class ApiDocsModel : PageModel
{
    private readonly ILogger<ApiDocsModel> _logger;

    public ApiDocsModel(ILogger<ApiDocsModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        _logger.LogInformation("API documentation page accessed at {Timestamp}", DateTime.UtcNow);
    }
}