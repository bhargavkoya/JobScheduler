using System.Text.Json;
using JobScheduler.Domain.Jobs;

namespace JobScheduler.Application.Jobs;

internal static class Mapping
{
    public static TemplateDto ToDto(this JobTemplate t) => new(
        t.Id,
        t.Name,
        t.Description,
        t.SupportedScheduleTypes.OrderBy(s => s).ToList(),
        t.Fields.Select(f => new TemplateFieldDto(f.Name, f.Label, f.Type, f.Required)).ToList(),
        new RetryPolicyDto(t.DefaultRetryPolicy.MaxAutoRetries, t.DefaultRetryPolicy.BackoffSeconds),
        t.IsApproved,
        t.RequiresApproval);

    public static JobDto ToDto(this Job j, DateTime nowUtc, AtRiskPolicy atRisk)
    {
        var reason = AtRiskEvaluator.Evaluate(j, nowUtc, atRisk.Threshold);
        return j.ToDto(reason);
    }

    private static JobDto ToDto(this Job j, string? atRiskReason) => new(
        j.Id,
        j.TemplateId,
        j.Template?.Name ?? string.Empty,
        j.Name,
        j.ScheduleType,
        j.RunAtUtc,
        j.RunAtUtc is { } utc ? Ist.FromUtc(utc) : null,
        j.Status,
        j.Team,
        j.OwnerId,
        new RetryPolicyDto(j.RetryPolicy.MaxAutoRetries, j.RetryPolicy.BackoffSeconds),
        JsonSerializer.Deserialize<Dictionary<string, string>>(j.ConfigJson) ?? new(),
        j.CreatedAtUtc,
        j.CanCancel,
        j.Template?.RequiresApproval ?? false,
        j.ApproverUserId,
        j.StatusChangedAtUtc,
        atRiskReason is not null,
        atRiskReason);
}
