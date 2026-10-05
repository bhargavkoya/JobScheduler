using JobScheduler.Domain.Users;

namespace JobScheduler.Domain.Jobs;

public class InvalidJobStateException(string message) : Exception(message);

public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TemplateId { get; set; }
    public JobTemplate? Template { get; set; }
    public string Name { get; set; } = string.Empty;
    public ScheduleType ScheduleType { get; set; }

    /// <summary>For Fixed jobs: the one-off run time, stored in UTC (entered in IST).</summary>
    public DateTime? RunAtUtc { get; set; }

    /// <summary>For Recurrent jobs: Quartz cron expression, evaluated in IST.</summary>
    public string? RecurrenceCron { get; set; }

    /// <summary>For Recurrent jobs: human-readable form, e.g. "Daily at 09:00 IST".</summary>
    public string? RecurrenceText { get; set; }

    /// <summary>For EventBased jobs: the job whose completion triggers this one.</summary>
    public Guid? TriggerJobId { get; set; }

    /// <summary>For TriggerBased jobs: SHA-256 (hex) of the webhook token. The token itself is shown once at creation.</summary>
    public string? WebhookTokenHash { get; set; }

    public string ConfigJson { get; set; } = "{}";
    public RetryPolicy RetryPolicy { get; set; } = new();
    public Guid OwnerId { get; set; }
    public Team Team { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Scheduled;

    /// <summary>The single approver, resolved at creation for templates that require approval.</summary>
    public Guid? ApproverUserId { get; set; }

    /// <summary>When <see cref="Status"/> last changed. Drives at-risk flagging (stuck running / waiting too long).</summary>
    public DateTime StatusChangedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Jobs that haven't started or are waiting on a human can still be cancelled.</summary>
    public bool CanCancel => Status is JobStatus.Scheduled or JobStatus.NeedsManualAction
        || (Repeats && Status is JobStatus.Completed or JobStatus.Failed);

    /// <summary>Whether a new run may start now. A recurrent or trigger-based job is ready again once its previous run completed.</summary>
    public bool CanStartRun => Status == JobStatus.Scheduled
        || (Repeats && Status == JobStatus.Completed);

    /// <summary>Recurrent and TriggerBased jobs fire again and again, so a completed run leaves them ready.</summary>
    private bool Repeats => ScheduleType is ScheduleType.Recurrent or ScheduleType.TriggerBased;

    /// <summary>Queued or retrying. Allowed from Scheduled (first run) or Failed (manual retry).</summary>
    public void MarkInProgress()
    {
        if (Status is not (JobStatus.Scheduled or JobStatus.Failed) && !CanStartRun)
            throw new InvalidJobStateException($"A job in status '{Status}' cannot be started.");
        SetStatus(JobStatus.InProgress);
    }

    public void MarkCompleted()
    {
        if (Status != JobStatus.InProgress)
            throw new InvalidJobStateException($"A job in status '{Status}' cannot be completed.");
        SetStatus(JobStatus.Completed);
    }

    public void MarkFailed()
    {
        if (Status != JobStatus.InProgress)
            throw new InvalidJobStateException($"A job in status '{Status}' cannot be failed.");
        SetStatus(JobStatus.Failed);
    }

    public void Cancel()
    {
        if (!CanCancel)
            throw new InvalidJobStateException($"A job in status '{Status}' cannot be cancelled.");
        SetStatus(JobStatus.Cancelled);
    }

    /// <summary>The pipeline finished and a human must decide (single approver).</summary>
    public void RequestApproval()
    {
        if (Status != JobStatus.InProgress)
            throw new InvalidJobStateException($"A job in status '{Status}' cannot ask for approval.");
        SetStatus(JobStatus.NeedsManualAction);
    }

    public void Approve()
    {
        if (Status != JobStatus.NeedsManualAction)
            throw new InvalidJobStateException($"A job in status '{Status}' is not waiting for approval.");
        SetStatus(JobStatus.Completed);
    }

    /// <summary>A rejected job is Failed, so the normal retry path can re-submit it.</summary>
    public void Reject()
    {
        if (Status != JobStatus.NeedsManualAction)
            throw new InvalidJobStateException($"A job in status '{Status}' is not waiting for approval.");
        SetStatus(JobStatus.Failed);
    }

    private void SetStatus(JobStatus status)
    {
        Status = status;
        StatusChangedAtUtc = DateTime.UtcNow;
    }
}
