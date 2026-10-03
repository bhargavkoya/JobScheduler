using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Common;

/// <summary>The authenticated caller, as seen by Application services (implemented from the JWT in Infrastructure).</summary>
public interface ICurrentUser
{
    Guid UserId { get; }
    Role Role { get; }
    Team Team { get; }
    IReadOnlyCollection<Team> ObserverTeams { get; }
    IReadOnlyCollection<string> Permissions { get; }
}

public class NotFoundException(string message) : Exception(message);
public class ForbiddenException(string message) : Exception(message);
public class ConflictException(string message) : Exception(message);
