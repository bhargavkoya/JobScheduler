using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Runs;

public sealed record RunStepDto(
    PipelineStep Step, StepStatus Status, int Attempt, string Output, DateTime StartedAtUtc, DateTime FinishedAtUtc);

public sealed record JobRunDto(
    Guid Id,
    Guid JobId,
    string IdempotencyKey,
    RunStatus Status,
    int Attempt,
    int AutoRetriesUsed,
    string? Error,
    PipelineStep? FailedStep,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? FinishedAtUtc,
    IReadOnlyList<RunStepDto> Steps,
    string? TriggerPayload = null);

public interface IJobRunService
{
    Task<JobRunDto> RunNowAsync(Guid jobId, string? idempotencyKey, CancellationToken ct);
    Task<JobRunDto> RetryAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<JobRunDto>> ListRunsAsync(Guid jobId, CancellationToken ct);
    Task<string> ExportRunsCsvAsync(Guid jobId, CancellationToken ct);
}

/// <summary>User-facing run operations: visibility and permission checks, then delegate to the orchestrator.</summary>
public class JobRunService(IJobStore jobs, IJobRunStore runs, IRunOrchestrator orchestrator, ICurrentUser me) : IJobRunService
{
    public async Task<JobRunDto> RunNowAsync(Guid jobId, string? idempotencyKey, CancellationToken ct)
    {
        var job = await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);
        if (job.ScheduleType != ScheduleType.Manual)
            throw new ValidationException("Only Manual kickoff jobs can be run on demand.");
        if (me.Role != Role.Admin && job.OwnerId != me.UserId)
            throw new ForbiddenException("Only the job owner or an admin can run a job.");

        return ToDto(await orchestrator.EnqueueManualAsync(jobId, idempotencyKey, me.UserId, ct));
    }

    public async Task<JobRunDto> RetryAsync(Guid jobId, CancellationToken ct)
    {
        await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);
        if (me.Role != Role.Admin && !me.Permissions.Contains(Permissions.RetryJobs))
            throw new ForbiddenException("Retrying a job requires the 'jobs.retry' permission.");

        return ToDto(await orchestrator.RetryAsync(jobId, me.UserId, ct));
    }

    public async Task<IReadOnlyList<JobRunDto>> ListRunsAsync(Guid jobId, CancellationToken ct)
    {
        await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);
        return (await runs.ListForJobAsync(jobId, ct)).Select(ToDto).ToList();
    }

    public async Task<string> ExportRunsCsvAsync(Guid jobId, CancellationToken ct) =>
        RunHistoryCsv.Build(await ListRunsAsync(jobId, ct));

    internal static JobRunDto ToDto(JobRun r) => new(
        r.Id,
        r.JobId,
        r.IdempotencyKey,
        r.Status,
        r.Attempt,
        r.AutoRetriesUsed,
        r.Error,
        r.FailedStep,
        r.CreatedAtUtc,
        r.StartedAtUtc,
        r.FinishedAtUtc,
        r.Steps.OrderBy(s => s.StartedAtUtc)
            .Select(s => new RunStepDto(s.Step, s.Status, s.Attempt, s.Output, s.StartedAtUtc, s.FinishedAtUtc))
            .ToList(),
        r.TriggerPayload);
}
