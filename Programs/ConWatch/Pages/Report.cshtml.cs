using ConPersistence.Report;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class ReportModel(IspReportBuilder builder, TimeProvider time) : PageModel
{
    public IspReport Report { get; private set; } = null!;

    public async Task OnGetAsync(DateTime? from, DateTime? to)
    {
        var toUtc = (to?.ToUniversalTime() ?? time.GetUtcNow().UtcDateTime);
        var fromUtc = (from?.ToUniversalTime() ?? toUtc.AddDays(-30));
        Report = await builder.BuildAsync(fromUtc, toUtc);
    }

    public static string Duration(TimeSpan span) =>
        span == TimeSpan.Zero ? "none"
        : span < TimeSpan.FromMinutes(2) ? $"{span.TotalSeconds:F0} seconds"
        : span < TimeSpan.FromHours(2) ? $"{span.TotalMinutes:F0} minutes"
        : $"{span.TotalHours:F1} hours";
}
