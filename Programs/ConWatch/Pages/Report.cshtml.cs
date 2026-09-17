using ConPersistence.Report;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class ReportModel(IspReportBuilder builder, TimeProvider time) : PageModel
{
    public IspReport Report { get; private set; } = null!;

    public async Task OnGetAsync(string? from, string? to)
    {
        // A page, not an API: an unparseable bound falls back to the default window.
        var toUtc = Parse(to) ?? time.GetUtcNow().UtcDateTime;
        var fromUtc = Parse(from) ?? toUtc.AddDays(-30);
        Report = await builder.BuildAsync(fromUtc, toUtc);
    }

    private static DateTime? Parse(string? text)
    {
        try { return Api.TimeRange.ParseUtc(text); }
        catch (ArgumentException) { return null; }
    }

    public static string Duration(TimeSpan span) =>
        span == TimeSpan.Zero ? "none"
        : span < TimeSpan.FromMinutes(2) ? $"{span.TotalSeconds:F0} seconds"
        : span < TimeSpan.FromHours(2) ? $"{span.TotalMinutes:F0} minutes"
        : $"{span.TotalHours:F1} hours";
}
