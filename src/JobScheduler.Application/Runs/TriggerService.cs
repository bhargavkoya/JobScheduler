using JobScheduler.Application.Auth;
using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;

namespace JobScheduler.Application.Runs;

/// <summary>Trigger-based jobs: an external system fires the job with a webhook call (PRD 4.3).</summary>
public interface ITriggerService
{
    /// <summary>
    /// Validates the token and queues one run. An unknown job, a non-trigger job and a wrong token all fail the same
    /// way (401) so a caller cannot probe which job ids exist. The same Idempotency-Key returns the same run.
    /// </summary>
    Task<JobRunDto> TriggerAsync(Guid jobId, string? token, string? idempotencyKey, string? payload, CancellationToken ct);
}

public class TriggerService(IJobStore jobs, IRunOrchestrator orchestrator) : ITriggerService
{
    public const int MaxPayloadLength = 8192;

    public async Task<JobRunDto> TriggerAsync(
        Guid jobId, string? token, string? idempotencyKey, string? payload, CancellationToken ct)
    {
        var job = await jobs.FindAsync(jobId, ct);
        if (job is null || job.ScheduleType != ScheduleType.TriggerBased || !WebhookTokens.Verify(token, job.WebhookTokenHash))
            throw new InvalidCredentialsException();

        if (payload is { Length: > MaxPayloadLength })
            throw new ValidationException($"Webhook payload is too large (max {MaxPayloadLength} characters).");

        var run = await orchestrator.EnqueueTriggeredAsync(
            jobId, idempotencyKey, string.IsNullOrWhiteSpace(payload) ? null : payload, ct);
        return JobRunService.ToDto(run);
    }
}
