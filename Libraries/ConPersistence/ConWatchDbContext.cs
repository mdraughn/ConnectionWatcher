using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence;

/// <summary>SQLite database: monitoring configuration + transition log + rollups.</summary>
public class ConWatchDbContext(DbContextOptions<ConWatchDbContext> options) : DbContext(options)
{
    public DbSet<ProfileRow> Profiles => Set<ProfileRow>();
    public DbSet<TargetRow> Targets => Set<TargetRow>();
    public DbSet<PoolRow> Pools => Set<PoolRow>();
    public DbSet<PoolMemberRow> PoolMembers => Set<PoolMemberRow>();
    public DbSet<TransitionRow> Transitions => Set<TransitionRow>();
    public DbSet<RollupRow> Rollups => Set<RollupRow>();
    public DbSet<GlitchRow> Glitches => Set<GlitchRow>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProfileRow>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.Name).UseCollation("NOCASE");
        });

        modelBuilder.Entity<TargetRow>(e =>
        {
            e.HasIndex(t => t.Name).IsUnique();
            e.Property(t => t.Name).UseCollation("NOCASE");
            e.HasOne<TargetRow>().WithMany().HasForeignKey(t => t.ParentId);
            e.HasOne<ProfileRow>().WithMany().HasForeignKey(t => t.ProfileId);
        });

        modelBuilder.Entity<PoolRow>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.Name).UseCollation("NOCASE");
            e.HasOne<TargetRow>().WithMany().HasForeignKey(p => p.ParentId);
            e.HasMany(p => p.Members).WithOne().HasForeignKey(m => m.PoolId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PoolMemberRow>(e =>
        {
            e.HasKey(m => new { m.PoolId, m.TargetId });
            e.HasOne<TargetRow>().WithMany().HasForeignKey(m => m.TargetId);
        });

        modelBuilder.Entity<TransitionRow>(e =>
        {
            e.HasIndex(t => new { t.SubjectId, t.TimestampUtc });
            e.HasIndex(t => t.TimestampUtc);
        });

        modelBuilder.Entity<GlitchRow>(e =>
        {
            e.HasIndex(g => g.StartUtc);
            e.HasIndex(g => new { g.TargetId, g.StartUtc });
        });

        modelBuilder.Entity<RollupRow>(e =>
        {
            e.HasIndex(r => new { r.TargetId, r.Resolution, r.PeriodStartUtc }).IsUnique();
        });

        modelBuilder.Entity<SettingRow>(e =>
        {
            e.HasKey(s => s.Key);
        });
    }
}
