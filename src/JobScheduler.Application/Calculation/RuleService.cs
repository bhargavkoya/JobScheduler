using System.Text.Json;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Calculation;

public sealed record RuleDto(
    Guid Id, string Name, string Description, string Type,
    IReadOnlyDictionary<string, string> Parameters, bool IsActive);

public sealed record SaveRuleRequest(
    string Name, string? Description, string Type, Dictionary<string, string>? Parameters, bool IsActive = true);

public interface IRuleService
{
    Task<IReadOnlyList<RuleDto>> ListAsync(CancellationToken ct);
    Task<RuleDto> CreateAsync(SaveRuleRequest request, CancellationToken ct);
    Task<RuleDto> UpdateAsync(Guid id, SaveRuleRequest request, CancellationToken ct);
    IReadOnlyList<string> RuleTypes();
}

public class RuleService(IRuleStore rules, IRuleRegistry registry) : IRuleService
{
    public async Task<IReadOnlyList<RuleDto>> ListAsync(CancellationToken ct) =>
        (await rules.ListAsync(ct)).Select(ToDto).ToList();

    public IReadOnlyList<string> RuleTypes() => registry.Types;

    public async Task<RuleDto> CreateAsync(SaveRuleRequest request, CancellationToken ct)
    {
        await ValidateAsync(request, null, ct);
        var rule = new CalculationRule();
        Apply(rule, request);
        await rules.AddAsync(rule, ct);
        await rules.SaveChangesAsync(ct);
        return ToDto(rule);
    }

    public async Task<RuleDto> UpdateAsync(Guid id, SaveRuleRequest request, CancellationToken ct)
    {
        var rule = await rules.FindAsync(id, ct) ?? throw new NotFoundException($"Rule '{id}' was not found.");
        await ValidateAsync(request, id, ct);
        Apply(rule, request);
        await rules.SaveChangesAsync(ct);
        return ToDto(rule);
    }

    private async Task ValidateAsync(SaveRuleRequest r, Guid? existingId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 200)
            throw new ValidationException("Rule name is required (max 200 characters).");
        if (r.Name.Contains(','))
            throw new ValidationException("Rule name cannot contain a comma (jobs list rule names separated by commas).");

        var impl = registry.Find(r.Type ?? string.Empty)
            ?? throw new ValidationException($"Unknown rule type '{r.Type}'. Known types: {string.Join(", ", registry.Types)}.");
        impl.Validate(r.Parameters ?? new());

        if (await rules.NameExistsAsync(r.Name.Trim(), existingId, ct))
            throw new ConflictException($"A rule named '{r.Name.Trim()}' already exists.");
    }

    private void Apply(CalculationRule rule, SaveRuleRequest r)
    {
        rule.Name = r.Name.Trim();
        rule.Description = (r.Description ?? string.Empty).Trim();
        rule.Type = registry.Find(r.Type)!.Type; // canonical casing
        rule.ParametersJson = JsonSerializer.Serialize(r.Parameters ?? new());
        rule.IsActive = r.IsActive;
    }

    private static RuleDto ToDto(CalculationRule r) => new(
        r.Id, r.Name, r.Description, r.Type,
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.ParametersJson) ?? new(), r.IsActive);
}
