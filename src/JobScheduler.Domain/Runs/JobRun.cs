namespace JobScheduler.Domain.Runs;

public enum RunStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3
}

/// <summary>The fixed POC pipeline: download -> calculate -> email. Order is the enum order.</summary>
public enum PipelineStep
{
    DownloadReport = 0,
    Calculate = 1,
    SendEmail = 2
}

public enum StepStatus
{
    Succeeded = 0,
    Failed = 1
}

public class JobRunStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public PipelineStep Step { get; set; }
    public StepStatus Status { get; set; }
    public int Attempt { get; set; }

    /// <summary>Step result (JSON/text) on success, or the error message on failure. Feeds later steps and the audit log.</summary>
    public string Output { get; set; } = string.Empty;

    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
}

/// <summary>
/// One execution of a job. The idempotency key is unique, so the same trigger can never create two runs,
/// and a retry resumes the same run, skipping steps that already succeeded.
/// </summary>
public class JobRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Null when fired by the scheduler.</summary>
    public Guid? TriggeredByUserId { get; set; }

    public RunStatus Status { get; set; } = RunStatus.Pending;

    /// <summary>How many times the pipeline has been attempted (first attempt = 1).</summary>
    public int Attempt { get; set; }

    /// <summary>Automatic retries consumed since the run was created or last manually retried.</summary>
    public int AutoRetriesUsed { get; set; }

    public string? Error { get; set; }
    public PipelineStep? FailedStep { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    public List<JobRunStep> Steps { get; set; } = new();

    public bool HasSucceeded(PipelineStep step) =>
        Steps.Any(s => s.Step == step && s.Status == StepStatus.Succeeded);
}
