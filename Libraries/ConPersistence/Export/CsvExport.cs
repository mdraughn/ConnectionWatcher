using System.Globalization;
using System.Text;
using ConPersistence.Entities;

namespace ConPersistence.Export;

/// <summary>
/// CSV rendering for measurement data. Invariant culture, ISO-8601 UTC
/// timestamps, RFC-4180 escaping.
/// </summary>
public static class CsvExport
{
    public const string TransitionHeader =
        "id,timestampUtc,subjectId,subjectName,subjectKind,from,to,trigger," +
        "successRate,sampleSize,burstLossRate,ancestorId,detail,verdict,avgRtt";

    public const string RollupHeader =
        "targetId,periodStartUtc,resolution,pingCount,successCount,rttAvg,rttMin,rttMax";

    public const string GlitchHeader =
        "id,targetId,targetName,class,startUtc,endUtc,lostCount,sampleCount,detail";

    public static string TransitionLine(TransitionRow r) => Line(
        r.Id.ToString(CultureInfo.InvariantCulture),
        Iso(r.TimestampUtc),
        r.SubjectId.ToString(),
        r.SubjectName,
        r.SubjectKind,
        r.FromState,
        r.ToState,
        r.Trigger,
        Num(r.SuccessRate),
        r.SampleSize?.ToString(CultureInfo.InvariantCulture) ?? "",
        Num(r.BurstLossRate),
        r.AncestorId?.ToString() ?? "",
        r.Detail ?? "",
        r.Verdict ?? "",
        r.AvgRtt.ToString(CultureInfo.InvariantCulture));

    public static string RollupLine(RollupRow r) => Line(
        r.TargetId.ToString(),
        Iso(r.PeriodStartUtc),
        r.Resolution,
        r.PingCount.ToString(CultureInfo.InvariantCulture),
        r.SuccessCount.ToString(CultureInfo.InvariantCulture),
        r.RttAvg.ToString(CultureInfo.InvariantCulture),
        r.RttMin.ToString(CultureInfo.InvariantCulture),
        r.RttMax.ToString(CultureInfo.InvariantCulture));

    public static string GlitchLine(GlitchRow g) => Line(
        g.Id.ToString(CultureInfo.InvariantCulture),
        g.TargetId.ToString(),
        g.TargetName,
        g.Class == 1 ? "Internet" : "Target",
        Iso(g.StartUtc),
        Iso(g.EndUtc),
        g.LostCount.ToString(CultureInfo.InvariantCulture),
        g.SampleCount.ToString(CultureInfo.InvariantCulture),
        g.Detail ?? "");

    public static IEnumerable<string> TransitionLines(IEnumerable<TransitionRow> rows) =>
        rows.Select(TransitionLine).Prepend(TransitionHeader);

    public static IEnumerable<string> RollupLines(IEnumerable<RollupRow> rows) =>
        rows.Select(RollupLine).Prepend(RollupHeader);

    public static IEnumerable<string> GlitchLines(IEnumerable<GlitchRow> rows) =>
        rows.Select(GlitchLine).Prepend(GlitchHeader);

    private static string Iso(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture);

    private static string Num(double? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static string Line(params string[] fields) =>
        string.Join(",", fields.Select(Escape));

    private static string Escape(string field)
    {
        if (field.IndexOfAny([',', '"', '\n', '\r']) < 0)
            return field;
        var sb = new StringBuilder("\"");
        sb.Append(field.Replace("\"", "\"\""));
        sb.Append('"');
        return sb.ToString();
    }
}
