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
            SupportedScheduleTypes = [ScheduleType.Fixed, ScheduleType.Manual],
            Fields =
            [
                Field("reportName", "Report name", FieldType.String, true),
                Field("recipients", "Result recipient", FieldType.Email, true),
                Field("toleranceAmount", "Tolerance amount", FieldType.Number, true)
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
                Field("amount", "Amount", FieldType.Number, true)
            ],
            DefaultRetryPolicy = new RetryPolicy { MaxAutoRetries = 1, BackoffSeconds = 60 },
            IsApproved = true
        }, ct);

        await AddIfMissingAsync(new JobTemplate
        {
            Name = "Follow-up Chaser",
            Description = "Email a team member who owes an action, optionally re-chasing after a delay.",
            SupportedScheduleTypes = [ScheduleType.Fixed, ScheduleType.Manual],
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
    }

    private static TemplateField Field(string name, string label, FieldType type, bool required) =>
        new() { Name = name, Label = label, Type = type, Required = required };

    private async Task AddIfMissingAsync(JobTemplate template, CancellationToken ct)
    {
        if (!await db.JobTemplates.AnyAsync(t => t.Name == template.Name, ct))
            db.JobTemplates.Add(template);
    }
}
