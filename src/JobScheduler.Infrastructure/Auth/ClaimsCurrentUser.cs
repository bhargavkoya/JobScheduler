using System.Security.Claims;
using JobScheduler.Application.Common;
using JobScheduler.Domain.Users;
using Microsoft.AspNetCore.Http;

namespace JobScheduler.Infrastructure.Auth;

/// <summary>Reads the caller's identity from the validated JWT principal of the current request.</summary>
public class ClaimsCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal Principal =>
        accessor.HttpContext?.User ?? throw new InvalidOperationException("No authenticated user in the current context.");

    public Guid UserId =>
        Guid.TryParse(Principal.FindFirst("sub")?.Value, out var id)
            ? id
            : throw new InvalidOperationException("Token has no valid 'sub' claim.");

    public Role Role => Enum.Parse<Role>(Principal.FindFirst(AppClaimTypes.Role)?.Value ?? nameof(Role.Employee));
    public Team Team => Enum.Parse<Team>(Principal.FindFirst(AppClaimTypes.Team)?.Value ?? nameof(Team.Business));

    public IReadOnlyCollection<Team> ObserverTeams =>
        Principal.FindAll(AppClaimTypes.ObserverTeam).Select(c => Enum.Parse<Team>(c.Value)).ToList();

    public IReadOnlyCollection<string> Permissions =>
        Principal.FindAll(AppClaimTypes.Permission).Select(c => c.Value).ToList();
}
