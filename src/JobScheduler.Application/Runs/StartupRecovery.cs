using JobScheduler.Application.Jobs;

namespace JobScheduler.Application.Runs;

public sealed record RecoveryResult(int JobsRescheduled, int RunsRequeued);

/// <summary>
/// The scheduler and queue are in-memory, so on startup the database is the source of truth:
/// re-register triggers for Scheduled Fixed jobs and re-queue runs that were interrupted.
/// Nothing scheduled is silently dropped across a restart; late jobs fire immediately.
/// </summary>
public class StartupRecovery(IJobStore jobs, IJobRunStore runs, IJobScheduler scheduler, IJobQueue queue)
{
    public async Task<RecoveryResult> RecoverAsync(CancellationToken ct)
    {
        var scheduled = await jobs.ListScheduledFixedAsync(ct);
        foreach (var job in scheduled)
            await scheduler.ScheduleFixedAsync(job.Id, job.RunAtUtc!.Value, ct);

        var unfinished = await runs.ListUnfinishedAsync(ct);
        foreach (var run in unfinished)
            await queue.EnqueueAsync(run.Id, null, ct);

        return new RecoveryResult(scheduled.Count, unfinished.Count);
    }
}
