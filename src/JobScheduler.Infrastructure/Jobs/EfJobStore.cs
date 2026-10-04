using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Jobs;

public class EfJobStore(JobSchedulerDbContext db) : IJobStore
{
    public Task<Job?> FindAsync(Guid id, CancellationToken ct) =>
        db.Jobs.Include(j => j.Template).FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task<IReadOnlyList<Job>> ListAsync(JobQuery q, CancellationToken ct)
    {
        var query = db.Jobs.AsNoTracking().Include(j => j.Template).AsQueryable();

        if (q.VisibleTeams is not null) query = query.Where(j => q.VisibleTeams.Contains(j.Team));
        if (q.Status is { } status) query = query.Where(j => j.Status == status);
        if (q.Team is { } team) query = query.Where(j => j.Team == team);
        if (q.ScheduleType is { } type) query = query.Where(j => j.ScheduleType == type);
        if (q.OwnerId is { } owner) query = query.Where(j => j.OwnerId == owner);
        if (q.CreatedFromUtc is { } from) query = query.Where(j => j.CreatedAtUtc >= from);
        if (q.CreatedToUtc is { } to) query = query.Where(j => j.CreatedAtUtc <= to);

        return await query.OrderByDescending(j => j.CreatedAtUtc).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Job>> ListScheduledFixedAsync(CancellationToken ct) =>
        await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Scheduled && j.ScheduleType == ScheduleType.Fixed && j.RunAtUtc != null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Job>> ListActiveRecurrentAsync(CancellationToken ct) =>
        await db.Jobs.AsNoTracking()
            .Where(j => j.ScheduleType == ScheduleType.Recurrent && j.Status != JobStatus.Cancelled && j.RecurrenceCron != null)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Job>> ListWaitingDependentsAsync(Guid triggerJobId, CancellationToken ct) =>
        await db.Jobs.Include(j => j.Template)
            .Where(j => j.ScheduleType == ScheduleType.EventBased && j.TriggerJobId == triggerJobId
                        && j.Status == JobStatus.Scheduled)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Job>> ListNeedingManualActionAsync(CancellationToken ct) =>
        await db.Jobs.AsNoTracking().Include(j => j.Template)
            .Where(j => j.Status == JobStatus.NeedsManualAction)
            .ToListAsync(ct);

    public async Task AddAsync(Job job, CancellationToken ct) => await db.Jobs.AddAsync(job, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
