using System.Globalization;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Application.Approvals;

/// <summary>"Remind again if no action is taken": one chaser email to the approver, scheduled when approval is requested.</summary>
public interface IApprovalFollowUp
{
    /// <summary>Schedules the chaser if the job's config has followUpAfterMinutes. Safe to call again (restart recovery).</summary>
    Task ScheduleAsync(Job job, CancellationToken ct);

    /// <summary>Called by the scheduler when the chaser is due. Does nothing unless the job is still waiting.</summary>
    Task FireAsync(Guid jobId, CancellationToken ct);
}

public class ApprovalFollowUp(
    IJobStore jobs, IJobRunStore runs, IUserStore users, IEmailSender email, IJobScheduler scheduler,
    ILogger<ApprovalFollowUp> log) : IApprovalFollowUp
{
    public const string ConfigKey = "followUpAfterMinutes";

    public async Task ScheduleAsync(Job job, CancellationToken ct)
    {
        var config = JobConfig.Read(job);
        if (!config.TryGetValue(ConfigKey, out var raw)
            || !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var minutes)
            || minutes <= 0)
            return;

        // Anchored on when the job started waiting, so a restart re-creates the same due time (late ones fire at once).
        var dueUtc = job.StatusChangedAtUtc.AddMinutes((double)minutes);
        await scheduler.ScheduleFollowUpAsync(job.Id, dueUtc, ct);
    }

    public async Task FireAsync(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.FindAsync(jobId, ct);
        if (job is null || job.Status != JobStatus.NeedsManualAction)
        {
            log.LogInformation("Follow-up for job {JobId} skipped: no longer waiting for action.", jobId);
            return;
        }

        var approver = job.ApproverUserId is { } id ? await users.FindByIdAsync(id, ct) : null;
        if (approver is null)
        {
            log.LogWarning("Follow-up for job {JobId} skipped: no approver found.", jobId);
            return;
        }

        var run = await runs.FindLatestForJobAsync(jobId, ct);
        var key = $"{run?.IdempotencyKey ?? job.Id.ToString()}:followup:{run?.Attempt ?? 0}"; // once per approval request
        await email.SendAsync(new EmailMessage(
            approver.Email,
            $"[Job Scheduler] Reminder: '{job.Name}' is still waiting for your approval",
            $"Job '{job.Name}' has been waiting for your decision since {job.StatusChangedAtUtc:yyyy-MM-dd HH:mm} UTC.\n\n" +
            "Open the Manual Action queue in the dashboard to approve or reject it.",
            key, NotificationEvent.FollowUpReminder), ct);
    }
}
