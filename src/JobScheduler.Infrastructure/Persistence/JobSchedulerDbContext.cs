using JobScheduler.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Persistence;

public class JobSchedulerDbContext : DbContext
{
    public JobSchedulerDbContext(DbContextOptions<JobSchedulerDbContext> options)
        : base(options)
    {
    }

    public DbSet<SystemInfo> SystemInfo => Set<SystemInfo>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SystemInfo>().HasData(new SystemInfo
        {
            Id = 1,
            Component = "JobScheduler.Api",
            InitializedAtUtc = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
        });

        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).HasMaxLength(256).IsRequired();
            b.HasIndex(u => u.Email).IsUnique();
            b.Property(u => u.PasswordHash).IsRequired();
            b.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);
            b.Property(u => u.PrimaryTeam).HasConversion<string>().HasMaxLength(32);
            b.HasMany(u => u.Claims).WithOne().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(u => u.ObserverTeams).WithOne().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserClaim>(b =>
        {
            b.HasKey(c => new { c.UserId, c.Permission });
            b.Property(c => c.Permission).HasMaxLength(64);
        });

        modelBuilder.Entity<UserTeamAccess>(b =>
        {
            b.HasKey(t => new { t.UserId, t.Team });
            b.Property(t => t.Team).HasConversion<string>().HasMaxLength(32);
        });
    }
}
