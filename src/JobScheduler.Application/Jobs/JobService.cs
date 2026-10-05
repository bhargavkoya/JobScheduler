using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;

namespace JobScheduler.Application.Jobs;

public interface IJobService
{
    Task<JobDto> CreateAsync(CreateJobRequest request, CancellationToken ct);
    Task<IReadOnlyList<JobDto>> ListAsync(JobFilter filter, CancellationToken ct);
    Task<JobDto> GetAsync(Guid id, CancellationToken ct);
    Task<JobDto> CancelAsync(Guid id, CancellationToken ct);
}

public class JobService(
    IJobStore jobs, ITemplateStore templates, ICurrentUser me, TimeProvider clock, IJobScheduler scheduler,
    IUserStore users, AtRiskPolicy atRisk) : IJobService
{
    /// <summary>Schedule types that can actually be created in this phase.</summary>
    public static readonly IReadOnlySet<ScheduleType> CreatableScheduleTypes =
        new HashSet<ScheduleType>
        { ScheduleType.Fixed, ScheduleType.Manual, ScheduleType.Recurrent, ScheduleType.EventBased, ScheduleType.TriggerBased };

    public async Task<JobDto> CreateAsync(CreateJobRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200)
            throw new ValidationException("Job name is required (max 200 characters).");

        var template = await templates.FindAsync(request.TemplateId, ct)
            ?? throw new NotFoundException($"Template '{request.TemplateId}' was not found.");
        if (!template.IsApproved)
            throw new ValidationException($"Template '{template.Name}' has not been approved by an admin.");

        if (!CreatableScheduleTypes.Contains(request.ScheduleType))
            throw new ValidationException($"Schedule type '{request.ScheduleType}' is not supported yet.");
        if (!template.SupportedScheduleTypes.Contains(request.ScheduleType))
            throw new ValidationException($"Template '{template.Name}' does not support '{request.ScheduleType}' scheduling.");

        DateTime? runAtUtc = null;
        if (request.ScheduleType == ScheduleType.Fixed)
        {
            if (request.RunAtIst is not { } runAtIst)
                throw new ValidationException("Fixed jobs require a run date/time (IST).");
            runAtUtc = Ist.ToUtc(runAtIst);
            if (runAtUtc <= clock.GetUtcNow().UtcDateTime)
                throw new ValidationException("Run time must be in the future.");
        }
        else if (request.RunAtIst is not null)
        {
            throw new ValidationException($"{request.ScheduleType} jobs must not have a run time.");
        }

        RecurrenceSchedule? recurrence = null;
        if (request.ScheduleType == ScheduleType.Recurrent)
            recurrence = RecurrenceBuilder.Build(request.Recurrence);
        else if (request.Recurrence is not null)
            throw new ValidationException("Only Recurrent jobs take a recurrence.");

        Guid? triggerJobId = null;
        if (request.ScheduleType == ScheduleType.EventBased)
            triggerJobId = (await ResolveTriggerJobAsync(request.TriggerJobId, ct)).Id;
        else if (request.TriggerJobId is not null)
            throw new ValidationException("Only EventBased jobs take a trigger job.");

        // TriggerBased jobs get a webhook secret: shown once in the create response, stored only as a hash.
        var webhookToken = request.ScheduleType == ScheduleType.TriggerBased ? WebhookTokens.Generate() : null;

        var config = ValidateConfig(template, request.Config);
        var approverId = await ResolveApproverAsync(template, config, ct);
        RetryValidation.Validate(request.RetryPolicy);
        var retry = request.RetryPolicy
            ?? new RetryPolicyDto(template.DefaultRetryPolicy.MaxAutoRetries, template.DefaultRetryPolicy.BackoffSeconds);

        var job = new Job
        {
            TemplateId = template.Id,
            Template = template,
            Name = request.Name.Trim(),
            ScheduleType = request.ScheduleType,
            RunAtUtc = runAtUtc,
            RecurrenceCron = recurrence?.Cron,
            RecurrenceText = recurrence?.Text,
            TriggerJobId = triggerJobId,
            WebhookTokenHash = webhookToken is null ? null : WebhookTokens.Hash(webhookToken),
            ConfigJson = JsonSerializer.Serialize(config),
            RetryPolicy = new RetryPolicy { MaxAutoRetries = retry.MaxAutoRetries, BackoffSeconds = retry.BackoffSeconds },
            OwnerId = me.UserId,
            Team = me.Team,
            Status = JobStatus.Scheduled,
            ApproverUserId = approverId,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            StatusChangedAtUtc = clock.GetUtcNow().UtcDateTime
        };

        await jobs.AddAsync(job, ct);
        await jobs.SaveChangesAsync(ct);

        if (job.ScheduleType == ScheduleType.Fixed)
            await scheduler.ScheduleFixedAsync(job.Id, job.RunAtUtc!.Value, ct);
        else if (job.ScheduleType == ScheduleType.Recurrent)
            await scheduler.ScheduleRecurrentAsync(job.Id, job.RecurrenceCron!, ct);
        return job.ToDto(Now(), atRisk) with { WebhookToken = webhookToken };
    }

    public async Task<IReadOnlyList<JobDto>> ListAsync(JobFilter filter, CancellationToken ct)
    {
        var query = new JobQuery(
            JobAccess.VisibleTeams(me), filter.Status, filter.Team, filter.ScheduleType, filter.OwnerId,
            filter.CreatedFromUtc, filter.CreatedToUtc);
        return (await jobs.ListAsync(query, ct)).Select(j => j.ToDto(Now(), atRisk)).ToList();
    }

    public async Task<JobDto> GetAsync(Guid id, CancellationToken ct) => (await LoadVisibleAsync(id, ct)).ToDto(Now(), atRisk);

    public async Task<JobDto> CancelAsync(Guid id, CancellationToken ct)
    {
        var job = await LoadVisibleAsync(id, ct);
        if (me.Role != Role.Admin && job.OwnerId != me.UserId)
            throw new ForbiddenException("Only the job owner or an admin can cancel a job.");

        try
        {
            job.Cancel();
        }
        catch (InvalidJobStateException ex)
        {
            throw new ConflictException(ex.Message);
        }

        await jobs.SaveChangesAsync(ct);
        await scheduler.UnscheduleAsync(job.Id, ct);
        return job.ToDto(Now(), atRisk);
    }

    /// <summary>The upstream job must exist, be visible to the creator and still be able to complete.</summary>
    private async Task<Job> ResolveTriggerJobAsync(Guid? triggerJobId, CancellationToken ct)
    {
        if (triggerJobId is null)
            throw new ValidationException("Event-based jobs require the job whose completion triggers them.");
        var upstream = await JobAccess.LoadVisibleAsync(jobs, me, triggerJobId.Value, ct);
        if (upstream.Status is JobStatus.Cancelled or JobStatus.Completed or JobStatus.Failed
            && upstream.ScheduleType != ScheduleType.Recurrent)
            throw new ValidationException($"The trigger job is {upstream.Status} and can no longer complete.");
        return upstream;
    }

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    /// <summary>Single approver: the template's approverEmail must be a registered user who is allowed to approve.</summary>
    private async Task<Guid?> ResolveApproverAsync(JobTemplate template, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        if (!template.RequiresApproval) return null;

        var email = config.FirstOrDefault(kv => string.Equals(kv.Key, "approverEmail", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(email))
            throw new ValidationException($"Template '{template.Name}' requires approval, so it needs an 'approverEmail' field.");

        var approver = await users.FindByEmailAsync(AuthService.NormalizeEmail(email), ct)
            ?? throw new ValidationException($"Approver '{email}' is not a registered user.");
        if (approver.Role != Role.Admin && approver.Claims.All(c => c.Permission != Permissions.ApproveJobs))
            throw new ValidationException($"'{email}' does not have permission to approve jobs.");
        return approver.Id;
    }

    private Task<Job> LoadVisibleAsync(Guid id, CancellationToken ct) => JobAccess.LoadVisibleAsync(jobs, me, id, ct);

    private static Dictionary<string, string> ValidateConfig(JobTemplate template, IReadOnlyDictionary<string, string>? supplied)
    {
        var values = supplied ?? new Dictionary<string, string>();
        var fields = template.Fields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        var unknown = values.Keys.Where(k => !fields.ContainsKey(k)).ToList();
        if (unknown.Count > 0)
            throw new ValidationException($"Unknown config field(s): {string.Join(", ", unknown)}.");

        var result = new Dictionary<string, string>();
        foreach (var field in template.Fields)
        {
            var raw = values.FirstOrDefault(kv => string.Equals(kv.Key, field.Name, StringComparison.OrdinalIgnoreCase)).Value?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                if (field.Required) throw new ValidationException($"'{field.Label}' is required.");
                continue;
            }

            switch (field.Type)
            {
                case FieldType.Number when !decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out _):
                    throw new ValidationException($"'{field.Label}' must be a number.");
                case FieldType.Email when !(MailAddress.TryCreate(raw, out var addr) && addr.Address == raw):
                    throw new ValidationException($"'{field.Label}' must be a valid email address.");
            }

            result[field.Name] = raw;
        }

        return result;
    }
}
