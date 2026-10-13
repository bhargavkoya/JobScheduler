using JobScheduler.Application.Reconciliation;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Reconciliation;

public class EfReconciliationStore(JobSchedulerDbContext db) : IReconciliationStore
{
    public void AddFile(InputFile file, byte[] content)
    {
        db.InputFiles.Add(file);
        db.InputFileContents.Add(new InputFileContent { FileId = file.Id, Bytes = content });
    }

    public async Task<IReadOnlyList<InputFile>> FindFilesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        await db.InputFiles.Where(f => ids.Contains(f.Id)).ToListAsync(ct);

    public async Task<IReadOnlyList<InputFile>> ListActiveFilesAsync(Guid jobId, CancellationToken ct) =>
        await db.InputFiles.Where(f => f.JobId == jobId && f.SupersededAtUtc == null)
            .OrderBy(f => f.Alias).ToListAsync(ct);

    public Task<byte[]?> GetContentAsync(Guid fileId, CancellationToken ct) =>
        db.InputFileContents.AsNoTracking().Where(c => c.FileId == fileId)
            .Select(c => (byte[]?)c.Bytes).FirstOrDefaultAsync(ct);

    public void AddRule(ReconciliationRule rule) => db.ReconciliationRules.Add(rule);

    public async Task<IReadOnlyList<ReconciliationRule>> ListRulesAsync(Guid jobId, CancellationToken ct) =>
        await db.ReconciliationRules.AsNoTracking().Where(r => r.JobId == jobId)
            .OrderBy(r => r.Position).ToListAsync(ct);

    public async Task ReplaceResultsAsync(Guid runId, IReadOnlyList<ReconciliationResult> results, CancellationToken ct)
    {
        // Delete + insert in one transaction: a retried run ends with exactly one set of results, never two.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ReconciliationResults.Where(r => r.RunId == runId).ExecuteDeleteAsync(ct);
        db.ReconciliationResults.AddRange(results);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Thousands of rows are never read again by this request; stop tracking them (the run and job stay tracked).
        foreach (var result in results) db.Entry(result).State = EntityState.Detached;
    }

    public async Task<IReadOnlyList<RuleCount>> CountByRuleAsync(Guid runId, CancellationToken ct)
    {
        var rows = await db.ReconciliationResults.AsNoTracking().Where(r => r.RunId == runId)
            .GroupBy(r => new { r.RuleId, r.RuleName })
            .Select(g => new
            {
                g.Key.RuleId, g.Key.RuleName,
                Total = g.Count(),
                Passed = g.Count(r => r.Status == RecordStatus.Pass)
            })
            .ToListAsync(ct);
        return rows.Select(r => new RuleCount(r.RuleId, r.RuleName, r.Total, r.Passed)).ToList();
    }

    public Task<int> CountAsync(Guid runId, RecordStatus? status, Guid? ruleId, CancellationToken ct) =>
        Filter(runId, status, ruleId).CountAsync(ct);

    public async Task<IReadOnlyList<ReconciliationResult>> QueryAsync(
        Guid runId, RecordStatus? status, Guid? ruleId, int skip, int take, CancellationToken ct) =>
        await Filter(runId, status, ruleId).OrderBy(r => r.Id).Skip(skip).Take(take).ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    private IQueryable<ReconciliationResult> Filter(Guid runId, RecordStatus? status, Guid? ruleId)
    {
        var query = db.ReconciliationResults.AsNoTracking().Where(r => r.RunId == runId);
        if (status is { } s) query = query.Where(r => r.Status == s);
        if (ruleId is { } id) query = query.Where(r => r.RuleId == id);
        return query;
    }
}
