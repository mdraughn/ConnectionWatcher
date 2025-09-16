using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConDiag.Pages;

/// <summary>
/// Page model for the main diagnostics dashboard.
/// </summary>
public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(ILogger<IndexModel> logger)
    {
        _logger = logger;
    }

    public void OnGet()
    {
        _logger.LogInformation("Dashboard page accessed at {Timestamp}", DateTime.UtcNow);
    }
}