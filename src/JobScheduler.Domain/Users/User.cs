namespace JobScheduler.Domain.Users;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public Role Role { get; set; } = Role.Employee;
    public Team PrimaryTeam { get; set; } = Team.Business;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<UserClaim> Claims { get; set; } = new();
    public List<UserTeamAccess> ObserverTeams { get; set; } = new();

    public void SetPermissions(IEnumerable<string> permissions)
    {
        Claims.Clear();
        foreach (var permission in permissions.Distinct(StringComparer.Ordinal))
            Claims.Add(new UserClaim { UserId = Id, Permission = permission });
    }

    public void SetObserverTeams(IEnumerable<Team> teams)
    {
        ObserverTeams.Clear();
        foreach (var team in teams.Distinct().Where(t => t != PrimaryTeam))
            ObserverTeams.Add(new UserTeamAccess { UserId = Id, Team = team });
    }
}
