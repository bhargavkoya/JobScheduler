using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Auth;

public class UserAdminService(IUserStore users) : IUserAdminService
{
    public async Task<IReadOnlyList<UserProfile>> ListUsersAsync(CancellationToken ct) =>
        (await users.ListAsync(ct)).Select(u => u.ToProfile()).ToList();

    public async Task<UserProfile> UpdateTeamAsync(Guid userId, UpdateTeamRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct) ?? throw new UserNotFoundException(userId);
        if (!Enum.IsDefined(request.PrimaryTeam) || !Enum.IsDefined(request.Role))
            throw new ValidationException("Unknown team or role.");

        user.PrimaryTeam = request.PrimaryTeam;
        user.Role = request.Role;
        user.SetObserverTeams(request.ObserverTeams ?? Array.Empty<Team>());

        await users.SaveChangesAsync(ct);
        return user.ToProfile();
    }

    public async Task<UserProfile> UpdateClaimsAsync(Guid userId, UpdateClaimsRequest request, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId, ct) ?? throw new UserNotFoundException(userId);
        var unknown = request.Permissions.Where(p => !Permissions.All.Contains(p)).ToList();
        if (unknown.Count > 0)
            throw new ValidationException($"Unknown permission(s): {string.Join(", ", unknown)}.");

        user.SetPermissions(request.Permissions);

        await users.SaveChangesAsync(ct);
        return user.ToProfile();
    }
}
