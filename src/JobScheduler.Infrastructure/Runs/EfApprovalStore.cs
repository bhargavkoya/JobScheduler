using JobScheduler.Application.Approvals;
using JobScheduler.Domain.Runs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Runs;

public class EfApprovalStore(JobSchedulerDbContext db) : IApprovalStore
{
    public async Task AddAsync(JobApproval approval, CancellationToken ct) => await db.JobApprovals.AddAsync(approval, ct);

    public async Task<IReadOnlyList<JobApproval>> ListForJobAsync(Guid jobId, CancellationToken ct) =>
        await db.JobApprovals.AsNoTracking().Where(a => a.JobId == jobId).ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
