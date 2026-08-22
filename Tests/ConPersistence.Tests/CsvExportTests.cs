using ConPersistence.Export;
using ConPersistence.Entities;

namespace ConPersistence.Tests;

public class CsvExportTests
{
    [Fact]
    public void Transition_rows_render_with_header_and_iso_timestamps()
    {
        var row = new TransitionRow
        {
            Id = 7,
            TimestampUtc = new DateTime(2026, 8, 22, 13, 5, 0, DateTimeKind.Utc),
            SubjectId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            SubjectName = "Google",
            SubjectKind = "Target",
            FromState = "Responding",
            ToState = "Down",
            Trigger = "ThresholdCrossed",
            SuccessRate = 0.05,
            SampleSize = 20,
            AvgRtt = 12.5f,
        };
        var lines = CsvExport.TransitionLines([row]).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("id,timestampUtc,subjectId,subjectName,subjectKind,from,to,trigger", lines[0]);
        Assert.Contains("2026-08-22T13:05:00", lines[1]);
        Assert.Contains("Google", lines[1]);
        Assert.Contains("0.05", lines[1]);
    }

    [Fact]
    public void Fields_with_commas_quotes_and_newlines_are_escaped()
    {
        var row = new TransitionRow
        {
            TimestampUtc = DateTime.UtcNow,
            SubjectId = Guid.NewGuid(),
            SubjectName = "weird, \"name\"",
            SubjectKind = "Target",
            FromState = "A",
            ToState = "B",
            Trigger = "T",
            Detail = "line1\nline2, with comma",
        };
        var line = CsvExport.TransitionLines([row]).Last();
        Assert.Contains("\"weird, \"\"name\"\"\"", line);
        Assert.Contains("\"line1\nline2, with comma\"", line);
    }

    [Fact]
    public void Rollup_rows_render_with_header()
    {
        var row = new RollupRow
        {
            TargetId = Guid.NewGuid(),
            PeriodStartUtc = new DateTime(2026, 8, 22, 13, 0, 0, DateTimeKind.Utc),
            Resolution = "Minute",
            PingCount = 60,
            SuccessCount = 59,
            RttAvg = 11.75,
            RttMin = 8,
            RttMax = 40,
        };
        var lines = CsvExport.RollupLines([row]).ToList();
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("targetId,periodStartUtc,resolution,pingCount,successCount,rttAvg,rttMin,rttMax", lines[0]);
        Assert.Contains("11.75", lines[1]);
        Assert.Contains("Minute", lines[1]);
    }
}
