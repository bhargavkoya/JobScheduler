using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Jobs;

public class EfTemplateStore(JobSchedulerDbContext db) : ITemplateStore
{
    public Task<JobTemplate?> FindAsync(Guid id, CancellationToken ct) =>
        db.JobTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken ct) =>
        db.JobTemplates.AnyAsync(t => t.Name == name && t.Id != excludingId, ct);

    public async Task<IReadOnlyList<JobTemplate>> ListAsync(bool includeUnapproved, CancellationToken ct) =>
        await db.JobTemplates.AsNoTracking()
            .Where(t => includeUnapproved || t.IsApproved)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

    public async Task AddAsync(JobTemplate template, CancellationToken ct) => await db.JobTemplates.AddAsync(template, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
