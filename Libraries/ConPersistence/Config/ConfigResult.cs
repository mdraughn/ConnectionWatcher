namespace ConPersistence.Config;

public enum ConfigErrorKind { None, Validation, NotFound, Conflict }

/// <summary>Outcome of a config mutation.</summary>
public record ConfigResult(bool Success, ConfigErrorKind Kind = ConfigErrorKind.None,
    IReadOnlyList<string>? Errors = null, Guid? Id = null)
{
    public static ConfigResult Ok(Guid? id = null) => new(true, Id: id);
    public static ConfigResult Fail(ConfigErrorKind kind, params string[] errors) =>
        new(false, kind, errors);
}
