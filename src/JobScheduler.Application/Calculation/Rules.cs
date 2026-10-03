using System.Globalization;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Runs;

namespace JobScheduler.Application.Calculation;

public sealed record RuleResult(string RuleName, decimal Value, bool Passed, string Message);

/// <summary>
/// One pluggable rule type. Implementations hold no per-rule state: the rule's parameters (stored as data)
/// are passed in on every call, so adding a rule type is one class and one DI registration.
/// </summary>
public interface IRule
{
    string Type { get; }

    /// <summary>Throws <see cref="ValidationException"/> if the parameters are unusable.</summary>
    void Validate(IReadOnlyDictionary<string, string> parameters);

    RuleResult Evaluate(string ruleName, ReportData report, IReadOnlyDictionary<string, string> parameters);
}

public interface IRuleRegistry
{
    IRule? Find(string type);
    IReadOnlyList<string> Types { get; }
}

public class RuleRegistry(IEnumerable<IRule> rules) : IRuleRegistry
{
    private readonly Dictionary<string, IRule> _rules =
        rules.ToDictionary(r => r.Type, StringComparer.OrdinalIgnoreCase);

    public IRule? Find(string type) => _rules.GetValueOrDefault(type);
    public IReadOnlyList<string> Types => _rules.Keys.Order().ToList();
}

internal static class RuleParams
{
    public static decimal? Decimal(IReadOnlyDictionary<string, string> p, string key)
    {
        if (!p.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return null;
        if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            throw new ValidationException($"Parameter '{key}' must be a number (got '{raw}').");
        return value;
    }

    public static decimal Required(IReadOnlyDictionary<string, string> p, string key) =>
        Decimal(p, key) ?? throw new ValidationException($"Parameter '{key}' is required.");

    /// <summary>Total of the report rows, optionally limited to one account.</summary>
    public static decimal Total(ReportData report, IReadOnlyDictionary<string, string> p) =>
        report.Rows
            .Where(r => !p.TryGetValue("account", out var account) || string.IsNullOrWhiteSpace(account)
                        || string.Equals(r.Account, account, StringComparison.OrdinalIgnoreCase))
            .Sum(r => r.Amount);

    public static string Fmt(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Sums report amounts (optionally for one account). Always passes; it just reports the figure.</summary>
public class SumAmountRule : IRule
{
    public string Type => "SumAmount";

    public void Validate(IReadOnlyDictionary<string, string> parameters) { }

    public RuleResult Evaluate(string ruleName, ReportData report, IReadOnlyDictionary<string, string> parameters)
    {
        var total = RuleParams.Total(report, parameters);
        return new RuleResult(ruleName, total, true, $"total {RuleParams.Fmt(total)}");
    }
}

/// <summary>Passes when the total is within [min, max]; either bound is optional.</summary>
public class ThresholdRule : IRule
{
    public string Type => "Threshold";

    public void Validate(IReadOnlyDictionary<string, string> parameters)
    {
        var min = RuleParams.Decimal(parameters, "min");
        var max = RuleParams.Decimal(parameters, "max");
        if (min is null && max is null)
            throw new ValidationException("Threshold needs at least one of 'min' or 'max'.");
        if (min > max)
            throw new ValidationException("Threshold 'min' cannot be greater than 'max'.");
    }

    public RuleResult Evaluate(string ruleName, ReportData report, IReadOnlyDictionary<string, string> parameters)
    {
        var total = RuleParams.Total(report, parameters);
        var min = RuleParams.Decimal(parameters, "min");
        var max = RuleParams.Decimal(parameters, "max");
        var passed = (min is null || total >= min) && (max is null || total <= max);
        var range = $"{(min is null ? "-inf" : RuleParams.Fmt(min.Value))}..{(max is null ? "+inf" : RuleParams.Fmt(max.Value))}";
        return new RuleResult(ruleName, total, passed, $"total {RuleParams.Fmt(total)} vs allowed {range}");
    }
}

/// <summary>Reconciliation check: passes when the total is within a percentage tolerance of an expected figure.</summary>
public class VarianceRule : IRule
{
    public string Type => "Variance";

    public void Validate(IReadOnlyDictionary<string, string> parameters)
    {
        var expected = RuleParams.Required(parameters, "expected");
        if (expected == 0) throw new ValidationException("Variance 'expected' cannot be zero.");
        if (RuleParams.Required(parameters, "tolerancePercent") < 0)
            throw new ValidationException("Variance 'tolerancePercent' cannot be negative.");
    }

    public RuleResult Evaluate(string ruleName, ReportData report, IReadOnlyDictionary<string, string> parameters)
    {
        var expected = RuleParams.Required(parameters, "expected");
        var tolerance = RuleParams.Required(parameters, "tolerancePercent");
        var total = RuleParams.Total(report, parameters);
        var variancePercent = Math.Abs(total - expected) / Math.Abs(expected) * 100m;
        var passed = variancePercent <= tolerance;
        return new RuleResult(ruleName, variancePercent, passed,
            $"variance {RuleParams.Fmt(Math.Round(variancePercent, 2))}% of expected {RuleParams.Fmt(expected)} (tolerance {RuleParams.Fmt(tolerance)}%)");
    }
}
