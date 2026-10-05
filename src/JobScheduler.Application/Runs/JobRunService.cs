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

    /// <summary>Run history in the requested format ("csv" or "pdf"). Unknown formats are a validation error.</summary>
    Task<ExportedFile> ExportRunsAsync(Guid jobId, string? format, CancellationToken ct);
}

/// <summary>User-facing run operations: visibility and permission checks, then delegate to the orchestrator.</summary>
public class JobRunService(
    IJobStore jobs, IJobRunStore runs, IRunOrchestrator orchestrator, ICurrentUser me,
    IEnumerable<IRunHistoryExporter> exporters, IUserStore users, TimeProvider clock, AtRiskPolicy atRisk) : IJobRunService
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

    public async Task<ExportedFile> ExportRunsAsync(Guid jobId, string? format, CancellationToken ct)
    {
        var wanted = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        var exporter = exporters.FirstOrDefault(e => e.Format == wanted)
            ?? throw new ValidationException(
                $"Unsupported export format '{format}'. Use one of: {string.Join(", ", exporters.Select(e => e.Format))}.");

        var job = await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);
        var owner = await users.FindByIdAsync(job.OwnerId, ct);
        var history = (await runs.ListForJobAsync(jobId, ct)).Select(ToDto).ToList();
        return exporter.Export(new RunHistoryReport(job.ToDto(clock.GetUtcNow().UtcDateTime, atRisk), owner?.Email, history));
    }

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
