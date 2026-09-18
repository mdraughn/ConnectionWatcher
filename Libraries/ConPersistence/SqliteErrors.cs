using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence;

/// <summary>Classifies SQLite failures that EF surfaces as DbUpdateException.</summary>
public static class SqliteErrors
{
    /// <summary>SQLITE_CONSTRAINT_UNIQUE (extended code 2067).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 };
}
