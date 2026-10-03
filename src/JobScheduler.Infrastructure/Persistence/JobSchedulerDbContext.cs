using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Persistence;

public class JobSchedulerDbContext : DbContext
{
    public JobSchedulerDbContext(DbContextOptions<JobSchedulerDbContext> options)
        : base(options)
    {
    }

    public DbSet<SystemInfo> SystemInfo => Set<SystemInfo>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SystemInfo>().HasData(new SystemInfo
        {
            Id = 1,
            Component = "JobScheduler.Api",
            InitializedAtUtc = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
        });
    }
}
