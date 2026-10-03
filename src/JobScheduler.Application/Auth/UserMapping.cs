using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Auth;

internal static class UserMapping
{
    public static UserProfile ToProfile(this User user) => new(
        user.Id,
        user.Email,
        user.Role,
        user.PrimaryTeam,
        user.ObserverTeams.Select(t => t.Team).OrderBy(t => t).ToList(),
        user.Claims.Select(c => c.Permission).OrderBy(p => p, StringComparer.Ordinal).ToList());
}
