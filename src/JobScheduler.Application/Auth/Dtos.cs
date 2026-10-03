using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Auth;

public sealed record RegisterRequest(string Email, string Password);
public sealed record LoginRequest(string Email, string Password);

public sealed record UserProfile(
    Guid Id,
    string Email,
    Role Role,
    Team PrimaryTeam,
    IReadOnlyList<Team> ObserverTeams,
    IReadOnlyList<string> Permissions);

public sealed record AuthResult(string Token, DateTime ExpiresAtUtc, UserProfile User);

public sealed record UpdateTeamRequest(Team PrimaryTeam, Role Role, IReadOnlyList<Team>? ObserverTeams);
public sealed record UpdateClaimsRequest(IReadOnlyList<string> Permissions);
