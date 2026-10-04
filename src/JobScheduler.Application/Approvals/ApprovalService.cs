using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Approvals;

public sealed record DecisionRequest(string? Comment);

public sealed record ApprovalDto(
    Guid Id, Guid JobId, Guid? RunId, Guid ApproverUserId, string? ApproverEmail,
    ApprovalDecision Decision, string Comment, DateTime DecidedAtUtc);

/// <summary>One row of the manual-action queue. CanDecide drives whether the action buttons are enabled.</summary>
public sealed record ManualActionItemDto(JobDto Job, string? ApproverEmail, bool CanDecide);

public interface IApprovalStore
{
    Task AddAsync(JobApproval approval, CancellationToken ct);
    Task<IReadOnlyList<JobApproval>> ListForJobAsync(Guid jobId, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IApprovalService
{
    Task<IReadOnlyList<ManualActionItemDto>> ListQueueAsync(CancellationToken ct);
    Task<IReadOnlyList<ApprovalDto>> ListForJobAsync(Guid jobId, CancellationToken ct);
    Task<JobDto> ApproveAsync(Guid jobId, string? comment, CancellationToken ct);
    Task<JobDto> RejectAsync(Guid jobId, string? comment, CancellationToken ct);
}

/// <summary>Single-approver workflow: only the job's named approver (or an admin) can decide, and only once.</summary>
public class ApprovalService(
    IJobStore jobs,
    IJobRunStore runs,
    IApprovalStore approvals,
    IUserStore users,
    IJobScheduler scheduler,
    IJobChainer chainer,
    ICurrentUser me,
    TimeProvider clock,
    AtRiskPolicy atRisk) : IApprovalService
{
    public async Task<IReadOnlyList<ManualActionItemDto>> ListQueueAsync(CancellationToken ct)
    {
        var query = new JobQuery(JobAccess.VisibleTeams(me), JobStatus.NeedsManualAction, null, null, null, null, null);
        var waiting = (await jobs.ListAsync(query, ct)).OrderBy(j => j.StatusChangedAtUtc).ToList();

        var emails = await EmailsAsync(waiting.Where(j => j.ApproverUserId is not null).Select(j => j.ApproverUserId!.Value), ct);
        return waiting
            .Select(j => new ManualActionItemDto(
                j.ToDto(Now(), atRisk),
                j.ApproverUserId is { } id ? emails.GetValueOrDefault(id) : null,
                CanDecide(j)))
            .ToList();
    }

    public async Task<IReadOnlyList<ApprovalDto>> ListForJobAsync(Guid jobId, CancellationToken ct)
    {
        await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);
        var history = await approvals.ListForJobAsync(jobId, ct);
        var emails = await EmailsAsync(history.Select(a => a.ApproverUserId), ct);
        return history
            .OrderBy(a => a.DecidedAtUtc)
            .Select(a => new ApprovalDto(a.Id, a.JobId, a.RunId, a.ApproverUserId, emails.GetValueOrDefault(a.ApproverUserId),
                a.Decision, a.Comment, a.DecidedAtUtc))
            .ToList();
    }

    public Task<JobDto> ApproveAsync(Guid jobId, string? comment, CancellationToken ct) =>
        DecideAsync(jobId, ApprovalDecision.Approved, comment, ct);

    public Task<JobDto> RejectAsync(Guid jobId, string? comment, CancellationToken ct) =>
        DecideAsync(jobId, ApprovalDecision.Rejected, comment, ct);

    private async Task<JobDto> DecideAsync(Guid jobId, ApprovalDecision decision, string? comment, CancellationToken ct)
    {
        var job = await JobAccess.LoadVisibleAsync(jobs, me, jobId, ct);

        // A second click (or a second approver) lands here once the job has moved on: 409, never a second decision.
        if (job.Status != JobStatus.NeedsManualAction)
            throw new ConflictException($"Job is {job.Status}; only jobs waiting for a manual action can be decided.");
        if (!CanDecide(job))
            throw new ForbiddenException("Only the job's approver (or an admin) can approve or reject it.");

        var trimmed = (comment ?? string.Empty).Trim();
        if (decision == ApprovalDecision.Rejected && trimmed.Length == 0)
            throw new ValidationException("A comment is required when rejecting.");
        if (trimmed.Length > 2000)
            throw new ValidationException("Comment is too long (max 2000 characters).");

        var run = await runs.FindLatestForJobAsync(jobId, ct);
        await approvals.AddAsync(new JobApproval
        {
            JobId = job.Id,
            RunId = run?.Id,
            ApproverUserId = me.UserId,
            Decision = decision,
            Comment = trimmed,
            DecidedAtUtc = clock.GetUtcNow().UtcDateTime
        }, ct);

        try
        {
            if (decision == ApprovalDecision.Approved) job.Approve(); else job.Reject();
        }
        catch (InvalidJobStateException ex)
        {
            throw new ConflictException(ex.Message);
        }

        await approvals.SaveChangesAsync(ct);
        await jobs.SaveChangesAsync(ct);
        await scheduler.UnscheduleAsync(job.Id, ct); // the follow-up chaser is no longer needed
        if (decision == ApprovalDecision.Approved && run is not null)
            await chainer.OnJobCompletedAsync(job.Id, run.Id, ct);
        return job.ToDto(Now(), atRisk);
    }

    /// <summary>Named approver or admin, and the approve permission (admins hold every permission).</summary>
    private bool CanDecide(Job job) =>
        me.Role == Role.Admin || (job.ApproverUserId == me.UserId && me.Permissions.Contains(Permissions.ApproveJobs));

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private async Task<Dictionary<Guid, string>> EmailsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var id in ids.Distinct())
            if (await users.FindByIdAsync(id, ct) is { } user)
                result[id] = user.Email;
        return result;
    }
}
