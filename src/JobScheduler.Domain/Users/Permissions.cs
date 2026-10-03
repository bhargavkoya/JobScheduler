namespace JobScheduler.Domain.Users;

/// <summary>
/// Fine-grained action claims. Role + team give broad access; these gate specific actions.
/// </summary>
public static class Permissions
{
    public const string ApproveJobs = "jobs.approve";
    public const string RetryJobs = "jobs.retry";
    public const string ViewOtherTeamsJobs = "jobs.view.otherTeams";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ApproveJobs,
        RetryJobs,
        ViewOtherTeamsJobs
    };
}
