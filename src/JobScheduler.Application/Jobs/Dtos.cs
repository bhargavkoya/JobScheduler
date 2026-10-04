using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Jobs;

public sealed record TemplateFieldDto(string Name, string Label, FieldType Type, bool Required);
public sealed record RetryPolicyDto(int MaxAutoRetries, int BackoffSeconds);

public sealed record TemplateDto(
    Guid Id,
    string Name,
    string Description,
    IReadOnlyList<ScheduleType> SupportedScheduleTypes,
    IReadOnlyList<TemplateFieldDto> Fields,
    RetryPolicyDto DefaultRetryPolicy,
    bool IsApproved,
    bool RequiresApproval);

public sealed record SaveTemplateRequest(
    string Name,
    string? Description,
    IReadOnlyList<ScheduleType> SupportedScheduleTypes,
    IReadOnlyList<TemplateFieldDto> Fields,
    RetryPolicyDto? DefaultRetryPolicy,
    bool RequiresApproval = false);

public sealed record CreateJobRequest(
    Guid TemplateId,
    string Name,
    ScheduleType ScheduleType,
    /// <summary>Fixed jobs only: local IST date/time (no offset).</summary>
    DateTime? RunAtIst,
    IReadOnlyDictionary<string, string>? Config,
    RetryPolicyDto? RetryPolicy,
    /// <summary>Recurrent jobs only.</summary>
    RecurrenceDto? Recurrence = null,
    /// <summary>EventBased jobs only: the job whose completion triggers this one.</summary>
    Guid? TriggerJobId = null);

public sealed record JobFilter(
    JobStatus? Status,
    Team? Team,
    ScheduleType? ScheduleType,
    Guid? OwnerId,
    DateTime? CreatedFromUtc,
    DateTime? CreatedToUtc);

public sealed record JobDto(
    Guid Id,
    Guid TemplateId,
    string TemplateName,
    string Name,
    ScheduleType ScheduleType,
    DateTime? RunAtUtc,
    DateTime? RunAtIst,
    JobStatus Status,
    Team Team,
    Guid OwnerId,
    RetryPolicyDto RetryPolicy,
    IReadOnlyDictionary<string, string> Config,
    DateTime CreatedAtUtc,
    bool CanCancel,
    bool RequiresApproval,
    Guid? ApproverUserId,
    DateTime StatusChangedAtUtc,
    bool IsAtRisk,
    string? AtRiskReason,
    string? RecurrenceText = null,
    Guid? TriggerJobId = null);
