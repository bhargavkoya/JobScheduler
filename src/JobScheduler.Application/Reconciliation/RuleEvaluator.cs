using System.Globalization;
using JobScheduler.Domain.Reconciliation;

namespace JobScheduler.Application.Reconciliation;

/// <summary>The result of one rule for one record, before it is stored against a run.</summary>
public sealed record RecordOutcome(
    string Key, int? LeftRow, int? RightRow, decimal? LeftValue, decimal? RightValue, decimal? Difference,
    RecordStatus Status, string Reason);

/// <summary>
/// Evaluates one reconciliation rule over two parsed files. A pure function of (rule, left, right): a retried run
/// recomputes exactly the same outcomes, so nothing can be double-counted.
/// </summary>
public static class RuleEvaluator
{
    public static IReadOnlyList<RecordOutcome> Evaluate(ReconciliationRule rule, CsvTable left, CsvTable right)
    {
        var leftExpr = Expression.Parse(rule.LeftExpression);
        var rightExpr = Expression.Parse(rule.RightExpression);
        var columns = BindColumns(rule, left, right, leftExpr, rightExpr);

        return rule.IsSingleFile
            ? EvaluateSingleFile(rule, left, leftExpr, rightExpr, columns)
            : EvaluateJoined(rule, left, right, leftExpr, rightExpr, columns);
    }

    // ---- joined: two files matched on a key ----

    private static List<RecordOutcome> EvaluateJoined(
        ReconciliationRule rule, CsvTable left, CsvTable right, Expression leftExpr, Expression rightExpr,
        Dictionary<string, BoundColumn> columns)
    {
        var leftKey = KeyIndex(rule.LeftKeyColumn, left, "left");
        var rightKey = KeyIndex(rule.RightKeyColumn, right, "right");

        var rightByKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < right.Rows.Count; i++)
        {
            var key = right.Rows[i][rightKey].Trim();
            if (key.Length == 0) continue;
            if (!rightByKey.TryGetValue(key, out var list)) rightByKey[key] = list = [];
            list.Add(i);
        }

        var leftCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in left.Rows)
        {
            var key = row[leftKey].Trim();
            if (key.Length > 0) leftCounts[key] = leftCounts.GetValueOrDefault(key) + 1;
        }

        var outcomes = new List<RecordOutcome>(left.Rows.Count);
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < left.Rows.Count; i++)
        {
            var key = left.Rows[i][leftKey].Trim();
            var leftRowNo = i + 1;

            if (key.Length == 0)
            {
                outcomes.Add(Fail("(blank)", leftRowNo, null, "The join key is empty in the left file."));
                continue;
            }

            matchedKeys.Add(key);

            if (!rightByKey.TryGetValue(key, out var partners))
            {
                outcomes.Add(Fail(key, leftRowNo, null, "No matching record in the right file."));
                continue;
            }

            if (leftCounts[key] > 1)
            {
                outcomes.Add(Fail(key, leftRowNo, partners[0] + 1,
                    $"Duplicate key: appears {leftCounts[key]} times in the left file."));
                continue;
            }

            if (partners.Count > 1)
            {
                outcomes.Add(Fail(key, leftRowNo, partners[0] + 1,
                    $"Duplicate key: appears {partners.Count} times in the right file (rows {string.Join(", ", partners.Select(p => p + 1))})."));
                continue;
            }

            outcomes.Add(Compare(rule, key, leftRowNo, partners[0] + 1, left.Rows[i], right.Rows[partners[0]],
                leftExpr, rightExpr, columns));
        }

        // Right-file records no left record pointed at.
        for (var j = 0; j < right.Rows.Count; j++)
        {
            var key = right.Rows[j][rightKey].Trim();
            if (key.Length == 0)
                outcomes.Add(Fail("(blank)", null, j + 1, "The join key is empty in the right file."));
            else if (!matchedKeys.Contains(key))
                outcomes.Add(Fail(key, null, j + 1, "No matching record in the left file."));
        }

        return outcomes;
    }

    // ---- single file: both sides read the same row ----

    private static List<RecordOutcome> EvaluateSingleFile(
        ReconciliationRule rule, CsvTable table, Expression leftExpr, Expression rightExpr,
        Dictionary<string, BoundColumn> columns)
    {
        var keyIndex = string.IsNullOrWhiteSpace(rule.LeftKeyColumn) ? -1 : KeyIndex(rule.LeftKeyColumn, table, "key");
        var outcomes = new List<RecordOutcome>(table.Rows.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var key = keyIndex >= 0 && row[keyIndex].Trim().Length > 0 ? row[keyIndex].Trim() : (i + 1).ToString(CultureInfo.InvariantCulture);
            outcomes.Add(Compare(rule, key, i + 1, null, row, row, leftExpr, rightExpr, columns));
        }

        return outcomes;
    }

    // ---- one record ----

    private static RecordOutcome Compare(
        ReconciliationRule rule, string key, int? leftRowNo, int? rightRowNo, string[] leftRow, string[] rightRow,
        Expression leftExpr, Expression rightExpr, Dictionary<string, BoundColumn> columns)
    {
        decimal Resolve(ColumnRef r)
        {
            var bound = columns[BindKey(r)];
            var cell = bound.OnLeft ? leftRow[bound.Index] : rightRow[bound.Index];
            return CellNumber.Parse(cell, bound.Label);
        }

        try
        {
            var l = leftExpr.Evaluate(Resolve);
            var r = rightExpr.Evaluate(Resolve);
            var diff = l - r;
            var (passed, reason) = Judge(rule, l, r, diff);
            return new RecordOutcome(key, leftRowNo, rightRowNo, l, r, diff,
                passed ? RecordStatus.Pass : RecordStatus.Fail, reason);
        }
        catch (CellValueException ex)
        {
            return Fail(key, leftRowNo, rightRowNo, ex.Message + ".");
        }
        catch (DivideByZeroException)
        {
            return Fail(key, leftRowNo, rightRowNo, "Division by zero in the expression.");
        }
        catch (OverflowException)
        {
            return Fail(key, leftRowNo, rightRowNo, "The calculation result is too large.");
        }
    }

    private static (bool Passed, string Reason) Judge(ReconciliationRule rule, decimal l, decimal r, decimal diff)
    {
        var allowed = rule.ToleranceMode == ToleranceMode.Percent ? Math.Abs(r) * rule.Tolerance / 100m : rule.Tolerance;
        var within = Math.Abs(diff) <= allowed;

        switch (rule.Operator)
        {
            case ComparisonOperator.Equal:
                if (within) return (true, diff == 0 ? "" : $"Within tolerance ({Num(allowed)}).");
                return (false, $"Left {Num(l)} vs right {Num(r)}: difference {Num(diff)} exceeds tolerance {Num(allowed)}.");
            case ComparisonOperator.NotEqual:
                return within
                    ? (false, $"Left {Num(l)} and right {Num(r)} are equal (within tolerance {Num(allowed)}).")
                    : (true, "");
            default:
                var ok = rule.Operator switch
                {
                    ComparisonOperator.LessThan => l < r,
                    ComparisonOperator.LessThanOrEqual => l <= r,
                    ComparisonOperator.GreaterThan => l > r,
                    _ => l >= r
                };
                return ok ? (true, "") : (false, $"Left {Num(l)} is not {OperatorText(rule.Operator)} right {Num(r)}.");
        }
    }

    public static string OperatorText(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "equal to",
        ComparisonOperator.NotEqual => "different from",
        ComparisonOperator.LessThan => "less than",
        ComparisonOperator.LessThanOrEqual => "less than or equal to",
        ComparisonOperator.GreaterThan => "greater than",
        _ => "greater than or equal to"
    };

    // ---- binding ----

    private sealed record BoundColumn(bool OnLeft, int Index, string Label);

    private static string BindKey(ColumnRef r) => $"{r.Alias.ToLowerInvariant()}.{r.Column.ToLowerInvariant()}";

    /// <summary>Resolves every column reference to a position up front, so a missing column fails once, clearly.</summary>
    private static Dictionary<string, BoundColumn> BindColumns(
        ReconciliationRule rule, CsvTable left, CsvTable right, Expression leftExpr, Expression rightExpr)
    {
        var bound = new Dictionary<string, BoundColumn>();
        foreach (var r in leftExpr.References.Concat(rightExpr.References))
        {
            var onLeft = string.Equals(r.Alias, rule.LeftAlias, StringComparison.OrdinalIgnoreCase);
            var onRight = string.Equals(r.Alias, rule.RightAlias, StringComparison.OrdinalIgnoreCase);
            if (!onLeft && !onRight)
                throw new InvalidOperationException($"Rule '{rule.Name}' refers to unknown file alias '{r.Alias}'.");

            var table = onLeft ? left : right;
            var index = table.IndexOf(r.Column);
            if (index < 0)
                throw new InvalidOperationException(
                    $"Rule '{rule.Name}': file '{r.Alias}' has no column '{r.Column}'. Columns are: {string.Join(", ", table.Headers)}.");
            bound[BindKey(r)] = new BoundColumn(onLeft, index, $"{r.Alias}.{table.Headers[index]}");
        }

        return bound;
    }

    private static int KeyIndex(string? column, CsvTable table, string side)
    {
        var index = string.IsNullOrWhiteSpace(column) ? -1 : table.IndexOf(column);
        if (index < 0)
            throw new InvalidOperationException(
                $"The {side} join key column '{column}' does not exist. Columns are: {string.Join(", ", table.Headers)}.");
        return index;
    }

    private static RecordOutcome Fail(string key, int? leftRow, int? rightRow, string reason) =>
        new(key, leftRow, rightRow, null, null, null, RecordStatus.Fail, reason);

    private static string Num(decimal value) => value.ToString("0.00##########", CultureInfo.InvariantCulture);
}
