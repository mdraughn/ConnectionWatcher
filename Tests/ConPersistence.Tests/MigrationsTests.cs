using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>
/// Unit tests build their schema with EnsureCreated, which never reads the
/// migrations; production runs MigrateAsync. This is the one check that the
/// two agree: a model change without a migration fails here, not on a user's
/// first start after upgrading.
/// </summary>
public class MigrationsTests
{
    [Fact]
    public void Migrations_capture_the_whole_model()
    {
        var options = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        using var db = new ConWatchDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges(),
            "The EF model has changes no migration captures. Add one with " +
            "`dotnet ef migrations add <Name> --project Libraries/ConPersistence` " +
            "(the project-only form: ConPersistence's EF Design reference is PrivateAssets=all, " +
            "so --startup-project Programs/ConWatch fails).");
    }
}
