using JobScheduler.Application.Notifications;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Runs;

public class EfNotificationPreferenceStore(JobSchedulerDbContext db) : INotificationPreferenceStore
{
    public async Task<IReadOnlyList<NotificationPreference>> ListForUserAsync(Guid userId, CancellationToken ct) =>
        await db.NotificationPreferences.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct);

    public Task<NotificationPreference?> FindAsync(Guid userId, NotificationEvent evt, CancellationToken ct) =>
        db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.Event == evt, ct);

    public async Task AddAsync(NotificationPreference preference, CancellationToken ct) =>
        await db.NotificationPreferences.AddAsync(preference, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
