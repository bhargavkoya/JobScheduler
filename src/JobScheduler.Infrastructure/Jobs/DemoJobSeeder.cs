using System.Text.Json;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Jobs;

/// <summary>
/// Seeds the demo chain (idempotent, keyed by job name): a Manual reconciliation job and an EventBased
/// follow-up that starts by itself when the reconciliation completes. Neither needs a Quartz trigger.
/// </summary>
public class DemoJobSeeder(JobSchedulerDbContext db, TimeProvider clock)
{
    public const string UpstreamName = "Demo: Daily Reconciliation";
    public const string FollowUpName = "Demo: Follow-up after Reconciliation";

    public async Task SeedAsync(CancellationToken ct)
    {
        if (await db.Jobs.AnyAsync(j => j.Name == UpstreamName, ct)) return;

        var owner = await db.Users.FirstOrDefaultAsync(u => u.Email == "business@jobscheduler.local", ct);
        var recon = await db.JobTemplates.FirstOrDefaultAsync(t => t.Name == "Daily Reconciliation Report", ct);
        var chaser = await db.JobTemplates.FirstOrDefaultAsync(t => t.Name == "Follow-up Chaser", ct);
        if (owner is null || recon is null || chaser is null) return;

        var now = clock.GetUtcNow().UtcDateTime;
        var upstream = new Job
        {
            TemplateId = recon.Id,
            Name = UpstreamName,
            ScheduleType = ScheduleType.Manual,
            ConfigJson = Json(new()
            {
                ["reportName"] = "daily-positions", ["recipients"] = owner.Email, ["toleranceAmount"] = "100",
                ["rules"] = "Total Position, Exposure Cap"
            }),
            RetryPolicy = new RetryPolicy { MaxAutoRetries = 1, BackoffSeconds = 5 },
            OwnerId = owner.Id, Team = Team.Business, CreatedAtUtc = now, StatusChangedAtUtc = now
        };
        db.Jobs.Add(upstream);
        db.Jobs.Add(new Job
        {
            TemplateId = chaser.Id,
            Name = FollowUpName,
            ScheduleType = ScheduleType.EventBased,
            TriggerJobId = upstream.Id,
            ConfigJson = Json(new() { ["assigneeEmail"] = owner.Email, ["message"] = "Reconciliation finished: please review the result." }),
            RetryPolicy = new RetryPolicy { MaxAutoRetries = 1, BackoffSeconds = 5 },
            OwnerId = owner.Id, Team = Team.Business, CreatedAtUtc = now, StatusChangedAtUtc = now
        });
        await db.SaveChangesAsync(ct);
    }

    private static string Json(Dictionary<string, string> d) => JsonSerializer.Serialize(d);
}
