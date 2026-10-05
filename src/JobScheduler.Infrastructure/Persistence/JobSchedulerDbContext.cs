using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
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
    public DbSet<JobTemplate> JobTemplates => Set<JobTemplate>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<JobRunStep> JobRunSteps => Set<JobRunStep>();
    public DbSet<CalculationRule> CalculationRules => Set<CalculationRule>();
    public DbSet<SentEmail> SentEmails => Set<SentEmail>();
    public DbSet<JobApproval> JobApprovals => Set<JobApproval>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

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

        modelBuilder.Entity<JobTemplate>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(t => t.Name).IsUnique();
            b.Property(t => t.Description).HasMaxLength(2000);
            b.PrimitiveCollection(t => t.SupportedScheduleTypes);
            b.OwnsMany(t => t.Fields, o => o.ToJson());
            b.OwnsOne(t => t.DefaultRetryPolicy);
        });

        modelBuilder.Entity<Job>(b =>
        {
            b.HasKey(j => j.Id);
            b.Property(j => j.Name).HasMaxLength(200).IsRequired();
            b.Property(j => j.ScheduleType).HasConversion<string>().HasMaxLength(32);
            b.Property(j => j.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(j => j.Team).HasConversion<string>().HasMaxLength(32);
            b.Property(j => j.WebhookTokenHash).HasMaxLength(64);
            b.Property(j => j.ConfigJson).HasColumnType("jsonb");
            b.OwnsOne(j => j.RetryPolicy);
            b.HasOne(j => j.Template).WithMany().HasForeignKey(j => j.TemplateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(j => j.OwnerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(j => j.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(j => j.Status);
            b.HasIndex(j => new { j.Team, j.CreatedAtUtc });
        });

        modelBuilder.Entity<JobRun>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.IdempotencyKey).HasMaxLength(200).IsRequired();
            b.HasIndex(r => r.IdempotencyKey).IsUnique();
            b.Property(r => r.TriggerPayload).HasMaxLength(8192);
            b.HasIndex(r => r.JobId);
            b.HasIndex(r => r.Status);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(r => r.FailedStep).HasConversion<string>().HasMaxLength(32);
            b.HasOne<Job>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(r => r.Steps).WithOne().HasForeignKey(st => st.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobRunStep>(b =>
        {
            b.HasKey(st => st.Id);
            // Steps are always added explicitly (never discovered through the navigation),
            // so EF must not treat a client-assigned Guid as an existing row.
            b.Property(st => st.Id).ValueGeneratedNever();
            b.Property(st => st.Step).HasConversion<string>().HasMaxLength(32);
            b.Property(st => st.Status).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<CalculationRule>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(r => r.Name).IsUnique();
            b.Property(r => r.Description).HasMaxLength(2000);
            b.Property(r => r.Type).HasMaxLength(64).IsRequired();
            b.Property(r => r.ParametersJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<JobApproval>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Decision).HasConversion<string>().HasMaxLength(32);
            b.Property(a => a.Comment).HasMaxLength(2000);
            b.HasIndex(a => a.JobId);
            b.HasOne<Job>().WithMany().HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(a => a.ApproverUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SentEmail>(b =>
        {
            b.HasKey(e => e.IdempotencyKey);
            b.Property(e => e.IdempotencyKey).HasMaxLength(300);
            b.Property(e => e.To).HasMaxLength(320).IsRequired();
            b.Property(e => e.Subject).HasMaxLength(500);
        });

        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.HasKey(p => new { p.UserId, p.Event });
            b.Property(p => p.Event).HasConversion<string>().HasMaxLength(32);
            b.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
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
