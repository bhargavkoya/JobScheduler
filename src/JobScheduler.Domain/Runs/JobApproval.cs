namespace JobScheduler.Domain.Runs;

public enum ApprovalDecision
{
    Approved = 0,
    Rejected = 1
}

/// <summary>Audit record of a single approver's decision on a job's run.</summary>
public class JobApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid? RunId { get; set; }
    public Guid ApproverUserId { get; set; }
    public ApprovalDecision Decision { get; set; }
    public string Comment { get; set; } = string.Empty;
    public DateTime DecidedAtUtc { get; set; } = DateTime.UtcNow;
}
