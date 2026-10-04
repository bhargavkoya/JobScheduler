using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Jobs;

public interface ITemplateStore
{
    Task<JobTemplate?> FindAsync(Guid id, CancellationToken ct);
    Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken ct);
    Task<IReadOnlyList<JobTemplate>> ListAsync(bool includeUnapproved, CancellationToken ct);
    Task AddAsync(JobTemplate template, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public sealed record JobQuery(
    IReadOnlyCollection<Team>? VisibleTeams, // null = every team
    JobStatus? Status,
    Team? Team,
    ScheduleType? ScheduleType,
    Guid? OwnerId,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc);

public interface IJobStore
{
    Task<Job?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Job>> ListAsync(JobQuery query, CancellationToken ct);
    /// <summary>Scheduled Fixed jobs with a run time (used to rebuild triggers on startup).</summary>
    Task<IReadOnlyList<Job>> ListScheduledFixedAsync(CancellationToken ct);
    /// <summary>Recurrent jobs that still repeat (not cancelled), used to rebuild cron triggers on startup.</summary>
    Task<IReadOnlyList<Job>> ListActiveRecurrentAsync(CancellationToken ct);
    /// <summary>Scheduled EventBased jobs waiting on the given upstream job.</summary>
    Task<IReadOnlyList<Job>> ListWaitingDependentsAsync(Guid triggerJobId, CancellationToken ct);
    /// <summary>Jobs waiting on a human, across all teams (used to rebuild follow-up triggers on startup).</summary>
    Task<IReadOnlyList<Job>> ListNeedingManualActionAsync(CancellationToken ct);
    Task AddAsync(Job job, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
