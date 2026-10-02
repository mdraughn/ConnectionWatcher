using ConCore.Targets;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
            e.Property(t => t.Scope).HasConversion<string>().HasMaxLength(16)
                .HasDefaultValue(TargetScope.Verdict);
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

        // Every DateTime column is UTC by construction (the *Utc suffix is the
        // rule), but SQLite has no kind and EF loads them Unspecified. Restore
        // the kind on read so JSON serializers emit the Z and no caller has to
        // SpecifyKind by hand. Storage format is unchanged: no migration.
        var utc = new ValueConverter<DateTime, DateTime>(
            v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties().Where(p => p.ClrType == typeof(DateTime)))
                property.SetValueConverter(utc);
    }
}
