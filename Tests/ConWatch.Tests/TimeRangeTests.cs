using ConWatch.Api;

namespace ConWatch.Tests;

public class TimeRangeTests
{
    private static readonly DateTime Midnight = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Offset_less_text_is_utc() =>
        Assert.Equal(Midnight, TimeRange.ParseUtc("2026-09-12T00:00:00"));

    [Fact]
    public void Z_is_utc() =>
        Assert.Equal(Midnight, TimeRange.ParseUtc("2026-09-12T00:00:00Z"));

    [Fact]
    public void Explicit_offset_is_honored() =>
        Assert.Equal(Midnight.AddHours(-2), TimeRange.ParseUtc("2026-09-12T00:00:00+02:00"));

    [Fact]
    public void Result_kind_is_utc() =>
        Assert.Equal(DateTimeKind.Utc, TimeRange.ParseUtc("2026-09-12T00:00:00")!.Value.Kind);

    [Fact]
    public void Null_and_blank_pass_through()
    {
        Assert.Null(TimeRange.ParseUtc(null));
        Assert.Null(TimeRange.ParseUtc("  "));
    }

    [Fact]
    public void Garbage_throws() =>
        Assert.Throws<ArgumentException>(() => TimeRange.ParseUtc("yesterday"));

    [Fact]
    public void AsUtc_treats_unspecified_kind_as_utc()
    {
        var unspecified = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(Midnight, TimeRange.AsUtc(unspecified));
        Assert.Equal(Midnight, TimeRange.AsUtc(Midnight));
    }

    [Fact]
    public void TryParseRange_reports_which_parameter_is_bad()
    {
        Assert.True(TimeRange.TryParseRange("2026-09-12T00:00:00", null, out var from, out var to, out var problem));
        Assert.Equal(Midnight, from);
        Assert.Null(to);
        Assert.Null(problem);

        Assert.False(TimeRange.TryParseRange("2026-09-12T00:00:00", "nope", out _, out _, out problem));
        Assert.NotNull(problem);
    }
}
