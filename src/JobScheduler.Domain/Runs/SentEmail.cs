namespace JobScheduler.Domain.Runs;

/// <summary>Record of an email that was actually sent. The idempotency key is the primary key, so a key can only be recorded once.</summary>
public class SentEmail
{
    public string IdempotencyKey { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}
