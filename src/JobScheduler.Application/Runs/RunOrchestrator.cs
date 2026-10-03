using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Runs;

/// <summary>System-level triggering (no HTTP user): used by the scheduler and by the user-facing service.</summary>
public interface IRunOrchestrator
{
    Task<JobRun> EnqueueScheduledAsync(Guid jobId, CancellationToken ct);
    Task<JobRun> EnqueueManualAsync(Guid jobId, string? clientKey, Guid triggeredByUserId, CancellationToken ct);
    Task<JobRun> RetryAsync(Guid jobId, Guid triggeredByUserId, CancellationToken ct);
}

public class RunOrchestrator(IJobStore jobs, IJobRunStore runs, IJobQueue queue, TimeProvider clock) : IRunOrchestrator
{
    private const int MaxClientKeyLength = 100;

    public async Task<JobRun> EnqueueScheduledAsync(Guid jobId, CancellationToken ct)
    {
        var job = await LoadAsync(jobId, ct);
        if (job.ScheduleType != ScheduleType.Fixed || job.RunAtUtc is null)
            throw new Auth.ValidationException("Only Fixed jobs with a run time are fired by the scheduler.");

        // One key per scheduled time: a duplicate fire (restart, misfire) maps to the same run.
        return await EnqueueAsync(job, $"{job.Id}:fixed:{job.RunAtUtc:O}", null, ct);
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

        if (job.Status != JobStatus.Scheduled)
            throw new ConflictException($"Job is {job.Status}; only Scheduled jobs can be run.");

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
