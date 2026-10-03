namespace JobScheduler.Domain.Users;

/// <summary>Secondary/observer access to a team other than the user's primary team.</summary>
public class UserTeamAccess
{
    public Guid UserId { get; set; }
    public Team Team { get; set; }
}
