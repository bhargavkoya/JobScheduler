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

    // Diff rather than clear-and-add: re-adding a row with the same composite key
    // in one SaveChanges would collide with the tracked row being removed.
    public void SetPermissions(IEnumerable<string> permissions)
    {
        var wanted = permissions.ToHashSet(StringComparer.Ordinal);
        Claims.RemoveAll(c => !wanted.Contains(c.Permission));
        foreach (var permission in wanted.Where(p => Claims.All(c => c.Permission != p)))
            Claims.Add(new UserClaim { UserId = Id, Permission = permission });
    }

    public void SetObserverTeams(IEnumerable<Team> teams)
    {
        var wanted = teams.Where(t => t != PrimaryTeam).ToHashSet();
        ObserverTeams.RemoveAll(t => !wanted.Contains(t.Team));
        foreach (var team in wanted.Where(t => ObserverTeams.All(o => o.Team != t)))
            ObserverTeams.Add(new UserTeamAccess { UserId = Id, Team = team });
    }
}
