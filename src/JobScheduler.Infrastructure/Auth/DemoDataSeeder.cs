using JobScheduler.Application.Auth;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Auth;

/// <summary>Seeds demo users (idempotent). Passwords are hashed at runtime, so this is not EF HasData.</summary>
public class DemoDataSeeder(JobSchedulerDbContext db, IPasswordHasher hasher)
{
    public const string DemoPassword = "Passw0rd!";

    public async Task SeedAsync(CancellationToken ct)
    {
        await SeedUserAsync("admin@jobscheduler.local", Role.Admin, Team.Technical,
            [Team.Business], [Permissions.ApproveJobs, Permissions.RetryJobs, Permissions.ViewOtherTeamsJobs], ct);
        await SeedUserAsync("business@jobscheduler.local", Role.Employee, Team.Business,
            [], [Permissions.ApproveJobs], ct);
        await SeedUserAsync("tech@jobscheduler.local", Role.Employee, Team.Technical,
            [Team.Business], [Permissions.RetryJobs], ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedUserAsync(string email, Role role, Team team, Team[] observer, string[] permissions, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return;

        var user = new User { Email = email, PasswordHash = hasher.Hash(DemoPassword), Role = role, PrimaryTeam = team };
        user.SetObserverTeams(observer);
        user.SetPermissions(permissions);
        db.Users.Add(user);
    }
}
