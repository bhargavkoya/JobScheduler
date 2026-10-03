namespace JobScheduler.Application.Auth;

public interface IUserAdminService
{
    Task<IReadOnlyList<UserProfile>> ListUsersAsync(CancellationToken ct);
    Task<UserProfile> UpdateTeamAsync(Guid userId, UpdateTeamRequest request, CancellationToken ct);
    Task<UserProfile> UpdateClaimsAsync(Guid userId, UpdateClaimsRequest request, CancellationToken ct);
}
