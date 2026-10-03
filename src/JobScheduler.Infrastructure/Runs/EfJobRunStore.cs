using JobScheduler.Application.Common;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Runs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Runs;

public class EfJobRunStore(JobSchedulerDbContext db) : IJobRunStore
{
    public Task<JobRun?> FindAsync(Guid id, CancellationToken ct) =>
        db.JobRuns.Include(r => r.Steps).FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<JobRun?> FindByKeyAsync(string idempotencyKey, CancellationToken ct) =>
        db.JobRuns.Include(r => r.Steps).FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct);

    public Task<JobRun?> FindLatestForJobAsync(Guid jobId, CancellationToken ct) =>
        db.JobRuns.Include(r => r.Steps).Where(r => r.JobId == jobId)
            .OrderByDescending(r => r.CreatedAtUtc).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<JobRun>> ListForJobAsync(Guid jobId, CancellationToken ct) =>
        await db.JobRuns.AsNoTracking().Include(r => r.Steps).Where(r => r.JobId == jobId)
            .OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct);

    public async Task<IReadOnlyList<JobRun>> ListUnfinishedAsync(CancellationToken ct) =>
        await db.JobRuns.AsNoTracking()
            .Where(r => r.Status == RunStatus.Pending || r.Status == RunStatus.Running)
            .ToListAsync(ct);

    public async Task AddAsync(JobRun run, CancellationToken ct) => await db.JobRuns.AddAsync(run, ct);

    public void AddStep(JobRunStep step) => db.JobRunSteps.Add(step);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Two triggers raced on the same idempotency key; the caller can safely retry and gets the existing run.
            throw new ConflictException("A run for this idempotency key already exists.");
        }
    }
}
