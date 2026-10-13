using System.Globalization;
using JobScheduler.Domain.Reconciliation;

namespace JobScheduler.Application.Reconciliation;

/// <summary>Human-readable renderings of a rule, for summaries, emails and exports.</summary>
public static class RuleText
{
    public static string Symbol(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "==",
        ComparisonOperator.NotEqual => "!=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        _ => ">="
    };

    /// <summary>For example: <c>a.Gross - a.Fee == b.Net, joined a.Id = b.Id, tolerance 0.05</c>.</summary>
    public static string Describe(ReconciliationRule rule)
    {
        var text = $"{rule.LeftExpression} {Symbol(rule.Operator)} {rule.RightExpression}";

        if (!rule.IsSingleFile)
            text += $", joined {rule.LeftAlias}.{rule.LeftKeyColumn} = {rule.RightAlias}.{rule.RightKeyColumn}";
        else if (!string.IsNullOrWhiteSpace(rule.LeftKeyColumn))
            text += $", keyed by {rule.LeftAlias}.{rule.LeftKeyColumn}";

        if (rule.Tolerance > 0 && rule.Operator is ComparisonOperator.Equal or ComparisonOperator.NotEqual)
        {
            var amount = rule.Tolerance.ToString("0.##########", CultureInfo.InvariantCulture);
            text += rule.ToleranceMode == ToleranceMode.Percent ? $", tolerance {amount}%" : $", tolerance {amount}";
        }

        return text;
    }
}
