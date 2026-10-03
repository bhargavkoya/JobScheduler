using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Runs;

public interface IJobRunStore
{
    /// <summary>Includes steps.</summary>
    Task<JobRun?> FindAsync(Guid id, CancellationToken ct);
    Task<JobRun?> FindByKeyAsync(string idempotencyKey, CancellationToken ct);
    Task<JobRun?> FindLatestForJobAsync(Guid jobId, CancellationToken ct);
    Task<IReadOnlyList<JobRun>> ListForJobAsync(Guid jobId, CancellationToken ct);

    /// <summary>Runs still Pending or Running (used to recover after a restart).</summary>
    Task<IReadOnlyList<JobRun>> ListUnfinishedAsync(CancellationToken ct);

    Task AddAsync(JobRun run, CancellationToken ct);

    /// <summary>Steps are added explicitly so EF always inserts them.</summary>
    void AddStep(JobRunStep step);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Async hand-off between triggers (scheduler, API) and the worker. Messages carry only a run id.</summary>
public interface IJobQueue
{
    Task EnqueueAsync(Guid runId, TimeSpan? delay, CancellationToken ct);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}

/// <summary>Seam over the time-based scheduler (Quartz).</summary>
public interface IJobScheduler
{
    Task ScheduleFixedAsync(Guid jobId, DateTime runAtUtc, CancellationToken ct);
    Task UnscheduleAsync(Guid jobId, CancellationToken ct);
}

public sealed record ReportRow(string Account, decimal Amount);
public sealed record ReportData(string ReportId, IReadOnlyList<ReportRow> Rows);

/// <summary>Internal finance app (REST). Mocked in the POC, but always called through this seam.</summary>
public interface IFinanceAppClient
{
    /// <summary>Idempotent per key: the same key returns the same report id.</summary>
    Task<string> TriggerReportAsync(string idempotencyKey, string reportName, CancellationToken ct);
    Task<ReportData> DownloadReportAsync(string reportId, CancellationToken ct);
}

public sealed record CalculationResult(string Summary, IReadOnlyDictionary<string, decimal> Values);

/// <summary>Rules-based, pluggable calculation engine. Real implementation arrives in Phase 4.</summary>
public interface ICalculationEngine
{
    Task<CalculationResult> CalculateAsync(
        Guid jobId, ReportData report, IReadOnlyDictionary<string, string> config, CancellationToken ct);
}

public sealed record EmailMessage(string To, string Subject, string Body, string IdempotencyKey);

public interface IEmailSender
{
    /// <summary>Implementations must not send twice for the same IdempotencyKey.</summary>
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

public sealed record StepContext(Job Job, JobRun Run, IReadOnlyDictionary<PipelineStep, string> PriorOutputs);

public interface IPipelineStep
{
    PipelineStep Name { get; }

    /// <summary>Returns the step output, persisted for later steps and the audit log.</summary>
    Task<string> ExecuteAsync(StepContext context, CancellationToken ct);
}
