using System.Text;
using System.Text.Json;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Runs;

namespace JobScheduler.Application.Calculation;

public interface IRuleStore
{
    Task<CalculationRule?> FindAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<CalculationRule>> FindByNamesAsync(IReadOnlyCollection<string> names, CancellationToken ct);
    Task<IReadOnlyList<CalculationRule>> ListAsync(CancellationToken ct);
    Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken ct);
    Task AddAsync(CalculationRule rule, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Runs the rules named in the job's "rules" config value (comma-separated) against the report.
/// A rule that does not pass is a reported result, not an error: the job still completes and the email says so.
/// Pure function of (report, rules), so a retried run recomputes the identical result and never double-counts.
/// </summary>
public class RulesCalculationEngine(IRuleStore rules, IRuleRegistry registry) : ICalculationEngine
{
    public const string RulesConfigKey = "rules";

    public async Task<CalculationResult> CalculateAsync(
        Guid jobId, ReportData report, IReadOnlyDictionary<string, string> config, CancellationToken ct)
    {
        var names = ParseNames(config);
        if (names.Count == 0)
            return new CalculationResult(
                $"No calculation rules configured. Report {report.ReportId} has {report.Rows.Count} rows.",
                new Dictionary<string, decimal>());

        var found = (await rules.FindByNamesAsync(names, ct)).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        var results = new List<RuleResult>();
        foreach (var name in names)
        {
            if (!found.TryGetValue(name, out var rule) || !rule.IsActive)
                throw new InvalidOperationException($"Calculation rule '{name}' does not exist or is inactive.");

            var impl = registry.Find(rule.Type)
                ?? throw new InvalidOperationException($"Rule '{name}' has unknown type '{rule.Type}'.");
            var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(rule.ParametersJson) ?? new();
            results.Add(impl.Evaluate(rule.Name, report, parameters));
        }

        var summary = new StringBuilder()
            .AppendLine($"{results.Count(r => r.Passed)} of {results.Count} rules passed (report {report.ReportId}):");
        foreach (var r in results)
            summary.AppendLine($"- {(r.Passed ? "PASS" : "FAIL")} {r.RuleName}: {r.Message}");

        return new CalculationResult(summary.ToString().TrimEnd(), results.ToDictionary(r => r.RuleName, r => r.Value));
    }

    private static List<string> ParseNames(IReadOnlyDictionary<string, string> config) =>
        config.TryGetValue(RulesConfigKey, out var raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
}
