using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Domain.Jobs;

namespace JobScheduler.Application.Jobs;

public interface ITemplateService
{
    Task<IReadOnlyList<TemplateDto>> ListAsync(bool includeUnapproved, CancellationToken ct);
    Task<TemplateDto> CreateAsync(SaveTemplateRequest request, CancellationToken ct);
    Task<TemplateDto> UpdateAsync(Guid id, SaveTemplateRequest request, CancellationToken ct);
    Task<TemplateDto> ApproveAsync(Guid id, CancellationToken ct);
}

public class TemplateService(ITemplateStore templates) : ITemplateService
{
    public async Task<IReadOnlyList<TemplateDto>> ListAsync(bool includeUnapproved, CancellationToken ct) =>
        (await templates.ListAsync(includeUnapproved, ct)).Select(t => t.ToDto()).ToList();

    public async Task<TemplateDto> CreateAsync(SaveTemplateRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, null, ct);
        var template = new JobTemplate();
        Apply(template, request);
        // New or edited templates always need (re-)approval before jobs can use them.
        template.IsApproved = false;

        await templates.AddAsync(template, ct);
        await templates.SaveChangesAsync(ct);
        return template.ToDto();
    }

    public async Task<TemplateDto> UpdateAsync(Guid id, SaveTemplateRequest request, CancellationToken ct)
    {
        var template = await templates.FindAsync(id, ct) ?? throw new NotFoundException($"Template '{id}' was not found.");
        await ValidateAsync(request, id, ct);
        Apply(template, request);
        template.IsApproved = false;

        await templates.SaveChangesAsync(ct);
        return template.ToDto();
    }

    public async Task<TemplateDto> ApproveAsync(Guid id, CancellationToken ct)
    {
        var template = await templates.FindAsync(id, ct) ?? throw new NotFoundException($"Template '{id}' was not found.");
        template.IsApproved = true;
        await templates.SaveChangesAsync(ct);
        return template.ToDto();
    }

    private static void Apply(JobTemplate template, SaveTemplateRequest r)
    {
        template.Name = r.Name.Trim();
        template.Description = (r.Description ?? string.Empty).Trim();
        template.SupportedScheduleTypes = r.SupportedScheduleTypes.Distinct().ToList();
        template.Fields = r.Fields
            .Select(f => new TemplateField
            {
                Name = f.Name.Trim(),
                Label = string.IsNullOrWhiteSpace(f.Label) ? f.Name.Trim() : f.Label.Trim(),
                Type = f.Type,
                Required = f.Required
            })
            .ToList();
        var retry = r.DefaultRetryPolicy ?? new RetryPolicyDto(3, 30);
        template.DefaultRetryPolicy = new RetryPolicy { MaxAutoRetries = retry.MaxAutoRetries, BackoffSeconds = retry.BackoffSeconds };
        template.RequiresApproval = r.RequiresApproval;
    }

    private async Task ValidateAsync(SaveTemplateRequest r, Guid? existingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200)
            throw new ValidationException("Template name is required (max 200 characters).");
        if (r.SupportedScheduleTypes is not { Count: > 0 } || r.SupportedScheduleTypes.Any(s => !Enum.IsDefined(s)))
            throw new ValidationException("At least one valid schedule type is required.");

        var names = r.Fields.Select(f => f.Name?.Trim() ?? string.Empty).ToList();
        if (names.Any(string.IsNullOrEmpty))
            throw new ValidationException("Every field needs a name.");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new ValidationException("Field names must be unique.");
        if (r.Fields.Any(f => !Enum.IsDefined(f.Type)))
            throw new ValidationException("Unknown field type.");

        RetryValidation.Validate(r.DefaultRetryPolicy);

        if (await templates.NameExistsAsync(r.Name.Trim(), existingId, ct))
            throw new ConflictException($"A template named '{r.Name.Trim()}' already exists.");
    }
}

internal static class RetryValidation
{
    public static void Validate(RetryPolicyDto? policy)
    {
        if (policy is null) return;
        if (policy.MaxAutoRetries is < 0 or > RetryPolicy.MaxRetriesLimit)
            throw new ValidationException($"Max auto retries must be between 0 and {RetryPolicy.MaxRetriesLimit}.");
        if (policy.BackoffSeconds is < 0 or > RetryPolicy.MaxBackoffSecondsLimit)
            throw new ValidationException($"Backoff must be between 0 and {RetryPolicy.MaxBackoffSecondsLimit} seconds.");
    }
}
