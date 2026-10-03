using JobScheduler.Application.Auth;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Auth;

public class EfUserStore(JobSchedulerDbContext db) : IUserStore
{
    private IQueryable<User> Query => db.Users.Include(u => u.Claims).Include(u => u.ObserverTeams);

    public Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct) =>
        Query.FirstOrDefaultAsync(u => u.Email == normalizedEmail, ct);

    public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Query.FirstOrDefaultAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct) =>
        await Query.AsNoTracking().OrderBy(u => u.Email).ToListAsync(ct);

    public async Task AddAsync(User user, CancellationToken ct) => await db.Users.AddAsync(user, ct);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Lost a race on the unique email index between the existence check and the insert.
            throw new DuplicateEmailException("(email)");
        }
    }
}
