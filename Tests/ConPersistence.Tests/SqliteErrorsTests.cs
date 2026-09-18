using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class SqliteErrorsTests
{
    [Fact]
    public void Unique_constraint_is_a_uniqueness_violation()
    {
        var inner = new SqliteException("UNIQUE constraint failed: Targets.Name", 19, 2067);
        Assert.True(SqliteErrors.IsUniqueViolation(new DbUpdateException("save failed", inner)));
    }

    [Fact]
    public void Other_constraints_and_other_causes_are_not()
    {
        var foreignKey = new SqliteException("FOREIGN KEY constraint failed", 19, 787);
        Assert.False(SqliteErrors.IsUniqueViolation(new DbUpdateException("save failed", foreignKey)));
        Assert.False(SqliteErrors.IsUniqueViolation(new DbUpdateException("save failed", new IOException("disk full"))));
    }
}
