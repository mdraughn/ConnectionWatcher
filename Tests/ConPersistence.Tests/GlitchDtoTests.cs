using ConPersistence.Entities;
using ConPersistence.Queries;

namespace ConPersistence.Tests;

public class GlitchDtoTests
{
    private static ScopeTags ScopeTagsFor()
    {
        using var factory = new TestDbFactory();
        using var db = factory.CreateDbContext();
        return ScopeTags.LoadAsync(db).GetAwaiter().GetResult();
    }

    [Fact]
    public void From_reads_an_unspecified_kind_as_utc()
    {
        var start = new DateTime(2026, 9, 4, 3, 34, 0, DateTimeKind.Unspecified);
        var dto = GlitchDto.From(new GlitchRow { Id = 7, TargetId = Guid.NewGuid(), TargetName = "Google",
            Class = 0, StartUtc = start, EndUtc = start.AddSeconds(9) }, ScopeTagsFor());
        Assert.Equal(TimeSpan.Zero, dto.StartUtc.Offset);
        Assert.Equal(start.Ticks, dto.StartUtc.UtcTicks);
        Assert.Equal(TimeSpan.Zero, dto.EndUtc.Offset);
        Assert.Equal(start.AddSeconds(9).Ticks, dto.EndUtc.UtcTicks);
    }

    [Fact]
    public void Class_names_and_scope_follow_the_class()
    {
        var tags = ScopeTagsFor();
        var at = new DateTime(2026, 9, 4, 3, 34, 0, DateTimeKind.Utc);
        var internet = GlitchDto.From(new GlitchRow { Id = 1, TargetId = Guid.NewGuid(), TargetName = "Internet",
            Class = 1, StartUtc = at, EndUtc = at }, tags);
        var gone = GlitchDto.From(new GlitchRow { Id = 2, TargetId = Guid.NewGuid(), TargetName = "Gone",
            Class = 0, StartUtc = at, EndUtc = at }, tags);

        Assert.Equal("Internet", internet.Class);
        Assert.Equal("Verdict", internet.Scope);
        Assert.Equal("Target", gone.Class);
        Assert.Equal("Unknown", gone.Scope);
    }
}
