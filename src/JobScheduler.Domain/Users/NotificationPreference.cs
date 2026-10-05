namespace JobScheduler.Domain.Users;

/// <summary>The kinds of email the app sends to people. A user can switch each one off (PRD 4.6).</summary>
public enum NotificationEvent
{
    JobCompleted = 0,
    JobFailed = 1,
    ApprovalRequested = 2,
    FollowUpReminder = 3
}

/// <summary>
/// One user's choice for one event. Only deviations are stored in practice, but a row can be either value;
/// a missing row means "enabled", so users who never opened the settings page keep getting every email.
/// </summary>
public class NotificationPreference
{
    public Guid UserId { get; set; }
    public NotificationEvent Event { get; set; }
    public bool Enabled { get; set; } = true;
}
