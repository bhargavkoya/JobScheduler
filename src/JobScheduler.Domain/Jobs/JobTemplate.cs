namespace JobScheduler.Domain.Jobs;

/// <summary>A required/optional input a job created from the template must supply. Stored as data, not code.</summary>
public class TemplateField
{
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public FieldType Type { get; set; } = FieldType.String;
    public bool Required { get; set; } = true;
}

public class RetryPolicy
{
    public const int MaxRetriesLimit = 10;
    public const int MaxBackoffSecondsLimit = 3600;

    public int MaxAutoRetries { get; set; } = 3;
    public int BackoffSeconds { get; set; } = 30;
}

public class JobTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<ScheduleType> SupportedScheduleTypes { get; set; } = new();
    public List<TemplateField> Fields { get; set; } = new();
    public RetryPolicy DefaultRetryPolicy { get; set; } = new();

    /// <summary>Only approved templates can be used to create jobs. Admins approve the catalog.</summary>
    public bool IsApproved { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
