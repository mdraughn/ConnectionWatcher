using ConEngine.Events;
using ConPersistence.Entities;

namespace ConPersistence.Queries;

/// <summary>
/// The one wire shape of a recorded glitch (GET /api/v1/glitches, the MCP get_glitches tool).
/// The times are DateTimeOffsets at offset zero, written "…+00:00".
/// </summary>
public sealed record GlitchDto(long Id, Guid TargetId, string TargetName, string Class,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, int LostCount, int SampleCount, string? Detail, string Scope)
{
    public static GlitchDto From(GlitchRow g, ScopeTags tags) => new(
        g.Id, g.TargetId, g.TargetName, ClassName(g.Class),
        // Tagged explicitly: every *Utc column is UTC by rule (see TransitionDto.From).
        new DateTimeOffset(DateTime.SpecifyKind(g.StartUtc, DateTimeKind.Utc)),
        new DateTimeOffset(DateTime.SpecifyKind(g.EndUtc, DateTimeKind.Utc)),
        g.LostCount, g.SampleCount, g.Detail,
        tags.Of(g.TargetId, g.Class == (int)GlitchClass.Internet ? "Verdict" : "Target"));

    public static string ClassName(int cls) => cls == (int)GlitchClass.Internet ? "Internet" : "Target";
}
