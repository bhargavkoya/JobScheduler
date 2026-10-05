using JobScheduler.Domain.Jobs;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Jobs;

/// <summary>Seeds the demo job catalog (idempotent, keyed by template name).</summary>
public class TemplateSeeder(JobSchedulerDbContext db)
{
    public async Task SeedAsync(CancellationToken ct)
    {
        await AddIfMissingAsync(new JobTemplate
        {
            Name = "Daily Reconciliation Report",
            Description = "Download the daily position report, run reconciliation rules and email the result.",
            SupportedScheduleTypes = [ScheduleType.Fixed, ScheduleType.Manual, ScheduleType.Recurrent],
            Fields =
            [
                Field("reportName", "Report name", FieldType.String, true),
                Field("recipients", "Result recipient", FieldType.Email, true),
                Field("toleranceAmount", "Tolerance amount", FieldType.Number, true),
                RulesField
            ],
            DefaultRetryPolicy = new RetryPolicy { MaxAutoRetries = 3, BackoffSeconds = 30 },
            IsApproved = true
        }, ct);

        await AddIfMissingAsync(new JobTemplate
        {
            Name = "Capital Allocation Approval",
            Description = "Prepare an allocation and wait for a single approver to sign it off.",
            SupportedScheduleTypes = [ScheduleType.Manual],
            Fields =
            [
                Field("portfolio", "Portfolio", FieldType.String, true),
                Field("approverEmail", "Approver email", FieldType.Email, true),
                Field("amount", "Amount", FieldType.Number, true),
                FollowUpField
            ],
            DefaultRetryPolicy = new RetryPolicy { MaxAutoRetries = 1, BackoffSeconds = 60 },
            IsApproved = true,
            RequiresApproval = true
        }, ct);

        await AddIfMissingAsync(new JobTemplate
        {
            Name = "Follow-up Chaser",
            Description = "Email a team member who owes an action, optionally re-chasing after a delay.",
            SupportedScheduleTypes = [ScheduleType.Fixed, ScheduleType.Manual, ScheduleType.EventBased],
            Fields =
            [
                Field("assigneeEmail", "Assignee email", FieldType.Email, true),
                Field("message", "Message", FieldType.Text, true),
                Field("remindAfterHours", "Remind again after (hours)", FieldType.Number, false)
            ],
            DefaultRetryPolicy = new RetryPolicy { MaxAutoRetries = 2, BackoffSeconds = 30 },
            IsApproved = true
        }, ct);

        // Deliberately unapproved, so the admin approval step can be demoed.
        await AddIfMissingAsync(new JobTemplate
        {
            Name = "Liquidity Report (Draft)",
            Description = "Draft template awaiting admin approval.",
            SupportedScheduleTypes = [ScheduleType.Fixed, ScheduleType.Manual],
            Fields = [Field("desk", "Desk", FieldType.String, true)],
            DefaultRetryPolicy = new RetryPolicy(),
            IsApproved = false
        }, ct);

        await db.SaveChangesAsync(ct);

        // Databases seeded before the rules engine existed: add the new field to the existing template.
        var recon = await db.JobTemplates.FirstAsync(t => t.Name == "Daily Reconciliation Report", ct);
        if (recon.Fields.All(f => f.Name != RulesField.Name))
        {
            recon.Fields = [.. recon.Fields, RulesField];
            await db.SaveChangesAsync(ct);
        }

        // Phase 6 added Recurrent / EventBased support to two catalog templates.
        await EnsureScheduleTypeAsync("Daily Reconciliation Report", ScheduleType.Recurrent, ct);
        await EnsureScheduleTypeAsync("Follow-up Chaser", ScheduleType.EventBased, ct);

        // Phase 7: the reconciliation report can also be fired by an external webhook.
        await EnsureScheduleTypeAsync("Daily Reconciliation Report", ScheduleType.TriggerBased, ct);

        // Same for the approval template: it gained RequiresApproval and the follow-up field in Phase 5.
        var approval = await db.JobTemplates.FirstAsync(t => t.Name == "Capital Allocation Approval", ct);
        if (!approval.RequiresApproval || approval.Fields.All(f => f.Name != FollowUpField.Name))
        {
            approval.RequiresApproval = true;
            if (approval.Fields.All(f => f.Name != FollowUpField.Name))
                approval.Fields = [.. approval.Fields, FollowUpField];
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task EnsureScheduleTypeAsync(string templateName, ScheduleType type, CancellationToken ct)
    {
        var template = await db.JobTemplates.FirstAsync(t => t.Name == templateName, ct);
        if (template.SupportedScheduleTypes.Contains(type)) return;
        template.SupportedScheduleTypes = [.. template.SupportedScheduleTypes, type];
        await db.SaveChangesAsync(ct);
    }

    private static TemplateField FollowUpField =>
        Field("followUpAfterMinutes", "Remind approver if no action after (minutes)", FieldType.Number, false);

    private static TemplateField RulesField =>
        Field("rules", "Calculation rules (comma-separated names)", FieldType.String, false);

    private static TemplateField Field(string name, string label, FieldType type, bool required) =>
        new() { Name = name, Label = label, Type = type, Required = required };

    private async Task AddIfMissingAsync(JobTemplate template, CancellationToken ct)
    {
        if (!await db.JobTemplates.AnyAsync(t => t.Name == template.Name, ct))
            db.JobTemplates.Add(template);
    }
}
