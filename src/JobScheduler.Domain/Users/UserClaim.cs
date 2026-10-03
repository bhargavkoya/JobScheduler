namespace JobScheduler.Domain.Users;

public class UserClaim
{
    public Guid UserId { get; set; }
    public string Permission { get; set; } = string.Empty;
}
