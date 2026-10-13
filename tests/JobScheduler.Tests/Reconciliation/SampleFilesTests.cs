using JobScheduler.Application.Reconciliation;
using JobScheduler.Domain.Reconciliation;

namespace JobScheduler.Tests.Reconciliation;

/// <summary>
/// Runs the documented sample rules over docs/samples/*.csv and checks the outcome lists in docs/samples/README.md.
/// If the data or the evaluator changes, this fails and the README must be updated with it.
/// </summary>
public class SampleFilesTests
{
    private static CsvTable Load(string name) =>
        CsvTable.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", name)));

    private static readonly CsvTable Trades = Load("1_internal_trades.csv");
    private static readonly CsvTable Custodian = Load("2_custodian_settlements.csv");
    private static readonly CsvTable Ledger = Load("3_gl_cash_postings.csv");

    private static ReconciliationRule Rule1(decimal tolerance = 0) => new()
    {
        Name = "Trade cash vs custodian", LeftAlias = "trades", RightAlias = "custodian",
        LeftKeyColumn = "TradeId", RightKeyColumn = "ClientTradeRef",
        LeftExpression = "trades.GrossCashAmount - trades.Commission - trades.Tax",
        Operator = ComparisonOperator.Equal, RightExpression = "custodian.CashAmount", Tolerance = tolerance
    };

    private static ReconciliationRule Rule2() => new()
    {
        Name = "Quantity vs custodian", LeftAlias = "trades", RightAlias = "custodian",
        LeftKeyColumn = "TradeId", RightKeyColumn = "ClientTradeRef",
        LeftExpression = "trades.Quantity", Operator = ComparisonOperator.Equal, RightExpression = "custodian.SettledQuantity"
    };

    private static ReconciliationRule Rule3(decimal tolerance = 0) => new()
    {
        Name = "Custodian vs ledger", LeftAlias = "custodian", RightAlias = "ledger",
        LeftKeyColumn = "ClientTradeRef", RightKeyColumn = "TradeId",
        LeftExpression = "custodian.CashAmount", Operator = ComparisonOperator.Equal,
        RightExpression = "ledger.DebitAmount - ledger.CreditAmount", Tolerance = tolerance
    };

    private static string[] FailedKeys(IReadOnlyList<RecordOutcome> outcomes, Func<RecordOutcome, bool>? where = null) =>
        outcomes.Where(o => o.Status == RecordStatus.Fail && (where?.Invoke(o) ?? true))
            .Select(o => o.Key).Order().ToArray();

    [Fact]
    public void Files_HaveTheDocumentedShape()
    {
        Assert.Equal((60, 13), (Trades.Rows.Count, Trades.Headers.Count));
        Assert.Equal((59, 9), (Custodian.Rows.Count, Custodian.Headers.Count));
        Assert.Equal((60, 10), (Ledger.Rows.Count, Ledger.Headers.Count));
    }

    [Fact]
    public void Rule1_CashVsCustodian_MatchesTheReadme()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule1(), Trades, Custodian);

        Assert.Equal(52, outcomes.Count(o => o.Status == RecordStatus.Pass));
        Assert.Equal(
            ["T-100003", "T-100012", "T-100019", "T-100026", "T-100044"],
            FailedKeys(outcomes, o => o.LeftRow is not null && o.RightRow is not null));
        Assert.Equal(["T-100007", "T-100031", "T-100052"], FailedKeys(outcomes, o => o.RightRow is null));
        Assert.Equal(["T-900001", "T-900002"], FailedKeys(outcomes, o => o.LeftRow is null));

        var big = outcomes.Single(o => o.Key == "T-100026");
        Assert.Equal(1200.00m, big.Difference); // difference = left (internal) minus right (custodian)
    }

    [Fact]
    public void Rule1_WithPennyTolerance_PassesOnlyTheRoundingDifferences()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule1(0.05m), Trades, Custodian);

        Assert.Equal(54, outcomes.Count(o => o.Status == RecordStatus.Pass));
        Assert.Equal(
            ["T-100012", "T-100019", "T-100026"],
            FailedKeys(outcomes, o => o.LeftRow is not null && o.RightRow is not null));
    }

    [Fact]
    public void Rule2_QuantityVsCustodian_MatchesTheReadme()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule2(), Trades, Custodian);

        Assert.Equal(
            ["T-100015", "T-100038"],
            FailedKeys(outcomes, o => o.LeftRow is not null && o.RightRow is not null));
        Assert.Equal(55, outcomes.Count(o => o.Status == RecordStatus.Pass));
    }

    [Fact]
    public void Rule3_CustodianVsLedger_MatchesTheReadme()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule3(), Custodian, Ledger);

        Assert.Equal(
            ["T-100003", "T-100005", "T-100012", "T-100017", "T-100019", "T-100026", "T-100029", "T-100044", "T-100050"],
            FailedKeys(outcomes, o => o.LeftRow is not null && o.RightRow is not null
                                      && !o.Reason.StartsWith("Duplicate")));
        Assert.Equal(["T-100010", "T-100041", "T-900001", "T-900002"], FailedKeys(outcomes, o => o.RightRow is null));
        Assert.Equal(["T-100007", "T-100031", "T-100052", "T-800001"], FailedKeys(outcomes, o => o.LeftRow is null));

        var duplicate = outcomes.Single(o => o.Reason.StartsWith("Duplicate"));
        Assert.Equal("T-100020", duplicate.Key);
        Assert.Contains("2 times in the right file", duplicate.Reason);
    }

    [Fact]
    public void Rule3_WithPennyTolerance_PassesThePennyDifferences()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule3(0.05m), Custodian, Ledger);

        var amountFailures = FailedKeys(outcomes, o => o.LeftRow is not null && o.RightRow is not null
                                                        && !o.Reason.StartsWith("Duplicate"));
        Assert.Equal(
            ["T-100005", "T-100012", "T-100017", "T-100019", "T-100026", "T-100029"], amountFailures);
    }

    [Fact]
    public void WithinFileRule_NetOfChargesColumnsOfTheLedger_AllPass()
    {
        // Debit and credit are never both filled on a ledger line: a within-file sanity check.
        var rule = new ReconciliationRule
        {
            Name = "One-sided posting", LeftAlias = "ledger", RightAlias = "ledger", LeftKeyColumn = "JournalId",
            LeftExpression = "ledger.DebitAmount * ledger.CreditAmount", Operator = ComparisonOperator.Equal,
            RightExpression = "0"
        };

        Assert.All(RuleEvaluator.Evaluate(rule, Ledger, Ledger), o => Assert.Equal(RecordStatus.Pass, o.Status));
    }
}
