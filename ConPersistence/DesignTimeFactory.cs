using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ConPersistence;

/// <summary>Lets `dotnet ef` create the context without a startup project.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<ConWatchDbContext>
{
    public ConWatchDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite("Data Source=design.db").Options);
}
