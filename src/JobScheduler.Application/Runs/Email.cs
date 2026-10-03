using JobScheduler.Application.Auth;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using Microsoft.Extensions.Logging;

namespace JobScheduler.Application.Runs;

/// <summary>The wire-level send (SMTP). Knows nothing about idempotency.</summary>
public interface IEmailTransport
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public interface ISentEmailStore
{
    Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>Returns false if the key was already recorded (for example by a concurrent attempt).</summary>
    Task<bool> TryRecordAsync(SentEmail email, CancellationToken ct);
}

/// <summary>
/// Sends each idempotency key at most once: skip if already recorded, send, then record.
/// If the process dies between the send and the record, the retry sends again, so delivery is at-least-once
/// in that narrow window. A failed send records nothing, so the retry can still send it.
/// </summary>
public class IdempotentEmailSender(
    IEmailTransport transport, ISentEmailStore sent, TimeProvider clock, ILogger<IdempotentEmailSender> log) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        if (await sent.ExistsAsync(message.IdempotencyKey, ct))
        {
            log.LogInformation("Email suppressed (already sent for key {Key}).", message.IdempotencyKey);
            return;
        }

        await transport.SendAsync(message.To, message.Subject, message.Body, ct);

        var recorded = await sent.TryRecordAsync(new SentEmail
        {
            IdempotencyKey = message.IdempotencyKey,
            To = message.To,
            Subject = message.Subject,
            SentAtUtc = clock.GetUtcNow().UtcDateTime
        }, ct);
        if (!recorded)
            log.LogWarning("Email for key {Key} was sent but another attempt had already recorded it.", message.IdempotencyKey);
    }
}

/// <summary>Called once a run has failed for good (auto-retries exhausted).</summary>
public interface IFailureNotifier
{
    Task NotifyFailedAsync(Job job, JobRun run, CancellationToken ct);
}

/// <summary>Emails the job owner the failed step and error (PRD 4.4). Idempotent per run attempt.</summary>
public class OwnerFailureNotifier(IUserStore users, IEmailSender email, ILogger<OwnerFailureNotifier> log) : IFailureNotifier
{
    public async Task NotifyFailedAsync(Job job, JobRun run, CancellationToken ct)
    {
        var owner = await users.FindByIdAsync(job.OwnerId, ct);
        if (owner is null)
        {
            log.LogWarning("Job {JobId} failed but its owner {OwnerId} was not found; no email sent.", job.Id, job.OwnerId);
            return;
        }

        // Attempt is part of the key: a redelivered failure is deduped, while a manual retry that fails again
        // is a new terminal failure and notifies again.
        var key = $"{run.IdempotencyKey}:failure:{run.Attempt}";
        var body = $"Job '{job.Name}' failed after {run.Attempt} attempt(s).\n\n" +
                   $"Failed step: {run.FailedStep}\nError: {run.Error}\n\n" +
                   "Open the job in the dashboard to retry it.";
        await email.SendAsync(new EmailMessage(owner.Email, $"[Job Scheduler] {job.Name} FAILED", body, key), ct);
    }
}
