using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Runs;

/// <summary>System-level triggering (no HTTP user): used by the scheduler and by the user-facing service.</summary>
public interface IRunOrchestrator
{
    /// <summary>Fixed or Recurrent firing. <paramref name="fireTimeUtc"/> is the trigger's scheduled time (idempotency per firing).</summary>
    Task<JobRun> EnqueueScheduledAsync(Guid jobId, DateTime? fireTimeUtc, CancellationToken ct);

    /// <summary>EventBased: the upstream job's run completed. One run per upstream run, however often this is called.</summary>
    Task<JobRun> EnqueueChainedAsync(Guid jobId, Guid upstreamRunId, CancellationToken ct);
    Task<JobRun> EnqueueManualAsync(Guid jobId, string? clientKey, Guid triggeredByUserId, CancellationToken ct);
    Task<JobRun> RetryAsync(Guid jobId, Guid triggeredByUserId, CancellationToken ct);
}

public class RunOrchestrator(IJobStore jobs, IJobRunStore runs, IJobQueue queue, TimeProvider clock) : IRunOrchestrator
{
    private const int MaxClientKeyLength = 100;

    public async Task<JobRun> EnqueueScheduledAsync(Guid jobId, DateTime? fireTimeUtc, CancellationToken ct)
    {
        var job = await LoadAsync(jobId, ct);
        switch (job.ScheduleType)
        {
            case ScheduleType.Fixed when job.RunAtUtc is not null:
                // One key per scheduled time: a duplicate fire (restart, misfire) maps to the same run.
                return await EnqueueAsync(job, $"{job.Id}:fixed:{job.RunAtUtc:O}", null, ct);
            case ScheduleType.Recurrent:
                // One key per firing, so a double-fire of the same cron tick never creates a second run.
                var fired = fireTimeUtc ?? clock.GetUtcNow().UtcDateTime;
                return await EnqueueAsync(job, $"{job.Id}:recurrent:{fired:O}", null, ct);
            default:
                throw new Auth.ValidationException("Only Fixed or Recurrent jobs are fired by the scheduler.");
        }
    }

    public async Task<JobRun> EnqueueChainedAsync(Guid jobId, Guid upstreamRunId, CancellationToken ct)
    {
        var job = await LoadAsync(jobId, ct);
        if (job.ScheduleType != ScheduleType.EventBased)
            throw new Auth.ValidationException("Only EventBased jobs are triggered by another job.");
        return await EnqueueAsync(job, $"{job.Id}:chain:{upstreamRunId}", null, ct);
    }

    public async Task<JobRun> EnqueueManualAsync(Guid jobId, string? clientKey, Guid triggeredByUserId, CancellationToken ct)
    {
        var job = await LoadAsync(jobId, ct);
        var suffix = string.IsNullOrWhiteSpace(clientKey)
            ? Guid.NewGuid().ToString("N")
            : clientKey.Trim()[..Math.Min(clientKey.Trim().Length, MaxClientKeyLength)];
        return await EnqueueAsync(job, $"{job.Id}:manual:{suffix}", triggeredByUserId, ct);
    }

    public async Task<JobRun> RetryAsync(Guid jobId, Guid triggeredByUserId, CancellationToken ct)
    {
        var job = await LoadAsync(jobId, ct);
        if (job.Status != JobStatus.Failed)
            throw new ConflictException($"Only failed jobs can be retried (job is {job.Status}).");

        var run = await runs.FindLatestForJobAsync(job.Id, ct)
            ?? throw new ConflictException("The job has no run to retry.");

        // Same run, same idempotency key: completed steps are skipped, so nothing is double-sent or double-counted.
        run.Status = RunStatus.Pending;
        run.AutoRetriesUsed = 0;
        run.Error = null;
        run.FailedStep = null;
        run.FinishedAtUtc = null;
        run.TriggeredByUserId = triggeredByUserId;
        job.MarkInProgress();

        await runs.SaveChangesAsync(ct);
        await jobs.SaveChangesAsync(ct);
        await queue.EnqueueAsync(run.Id, null, ct);
        return run;
    }

    private async Task<JobRun> EnqueueAsync(Job job, string key, Guid? userId, CancellationToken ct)
    {
        // Idempotency: the same trigger returns the existing run and enqueues nothing.
        var existing = await runs.FindByKeyAsync(key, ct);
        if (existing is not null) return existing;

        if (!job.CanStartRun)
            throw new ConflictException($"Job is {job.Status}; only jobs that are ready (Scheduled) can be run.");

        var run = new JobRun
        {
            JobId = job.Id,
            IdempotencyKey = key,
            TriggeredByUserId = userId,
            Status = RunStatus.Pending,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        job.MarkInProgress(); // queued counts as in progress; also blocks a second concurrent run

        await runs.AddAsync(run, ct);
        await runs.SaveChangesAsync(ct);
        await jobs.SaveChangesAsync(ct);
        await queue.EnqueueAsync(run.Id, null, ct);
        return run;
    }

    private async Task<Job> LoadAsync(Guid jobId, CancellationToken ct) =>
        await jobs.FindAsync(jobId, ct) ?? throw new NotFoundException($"Job '{jobId}' was not found.");
}
