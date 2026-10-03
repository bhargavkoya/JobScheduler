using JobScheduler.Application.Calculation;
using JobScheduler.Domain.Runs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Runs;

public class EfRuleStore(JobSchedulerDbContext db) : IRuleStore
{
    public Task<CalculationRule?> FindAsync(Guid id, CancellationToken ct) =>
        db.CalculationRules.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<CalculationRule>> FindByNamesAsync(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        var lowered = names.Select(n => n.ToLower()).ToList();
        return await db.CalculationRules.AsNoTracking().Where(r => lowered.Contains(r.Name.ToLower())).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CalculationRule>> ListAsync(CancellationToken ct) =>
        await db.CalculationRules.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);

    public Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken ct) =>
        db.CalculationRules.AnyAsync(r => r.Name == name && r.Id != excludingId, ct);

    public async Task AddAsync(CalculationRule rule, CancellationToken ct) => await db.CalculationRules.AddAsync(rule, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
