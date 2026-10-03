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
    IJobStore jobs, ITemplateStore templates, ICurrentUser me, TimeProvider clock, IJobScheduler scheduler) : IJobService
{
    /// <summary>Schedule types that can actually be created in this phase.</summary>
    public static readonly IReadOnlySet<ScheduleType> CreatableScheduleTypes =
        new HashSet<ScheduleType> { ScheduleType.Fixed, ScheduleType.Manual };

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
            throw new ValidationException("Manual kickoff jobs must not have a run time.");
        }

        var config = ValidateConfig(template, request.Config);
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
            ConfigJson = JsonSerializer.Serialize(config),
            RetryPolicy = new RetryPolicy { MaxAutoRetries = retry.MaxAutoRetries, BackoffSeconds = retry.BackoffSeconds },
            OwnerId = me.UserId,
            Team = me.Team,
            Status = JobStatus.Scheduled,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };

        await jobs.AddAsync(job, ct);
        await jobs.SaveChangesAsync(ct);

        if (job.ScheduleType == ScheduleType.Fixed)
            await scheduler.ScheduleFixedAsync(job.Id, job.RunAtUtc!.Value, ct);
        return job.ToDto();
    }

    public async Task<IReadOnlyList<JobDto>> ListAsync(JobFilter filter, CancellationToken ct)
    {
        var query = new JobQuery(
            JobAccess.VisibleTeams(me), filter.Status, filter.Team, filter.ScheduleType, filter.OwnerId,
            filter.CreatedFromUtc, filter.CreatedToUtc);
        return (await jobs.ListAsync(query, ct)).Select(j => j.ToDto()).ToList();
    }

    public async Task<JobDto> GetAsync(Guid id, CancellationToken ct) => (await LoadVisibleAsync(id, ct)).ToDto();

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
        return job.ToDto();
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
