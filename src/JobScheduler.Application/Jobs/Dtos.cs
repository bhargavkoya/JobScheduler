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
    bool IsApproved);

public sealed record SaveTemplateRequest(
    string Name,
    string? Description,
    IReadOnlyList<ScheduleType> SupportedScheduleTypes,
    IReadOnlyList<TemplateFieldDto> Fields,
    RetryPolicyDto? DefaultRetryPolicy);

public sealed record CreateJobRequest(
    Guid TemplateId,
    string Name,
    ScheduleType ScheduleType,
    /// <summary>Fixed jobs only: local IST date/time (no offset).</summary>
    DateTime? RunAtIst,
    IReadOnlyDictionary<string, string>? Config,
    RetryPolicyDto? RetryPolicy);

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
    bool CanCancel);
