using JobScheduler.Application.Reconciliation;
using JobScheduler.Domain.Reconciliation;

namespace JobScheduler.Tests.Reconciliation;

public class RuleEvaluatorTests
{
    private static CsvTable T(string csv) => CsvTableTests.Parse(csv);

    private static ReconciliationRule Rule(
        string left, ComparisonOperator op, string right, decimal tolerance = 0,
        ToleranceMode mode = ToleranceMode.Absolute, string a = "a", string b = "b",
        string? keyA = "Id", string? keyB = "Id") => new()
    {
        Name = "r", LeftAlias = a, RightAlias = b, LeftKeyColumn = keyA, RightKeyColumn = keyB,
        LeftExpression = left, Operator = op, RightExpression = right, Tolerance = tolerance, ToleranceMode = mode
    };

    private static RecordOutcome Single(ReconciliationRule rule, string left, string right) =>
        Assert.Single(RuleEvaluator.Evaluate(rule, T(left), T(right)));

    // ---- the user's example: col1 - col3 of file 1 should equal col2 of file 2 ----

    [Fact]
    public void FileOneColumnsMinusEachOther_EqualFileTwoColumn_Passes()
    {
        var rule = Rule("a.Gross - a.Fee", ComparisonOperator.Equal, "b.Net");
        var o = Single(rule, "Id,Gross,Fee\nX1,1000.00,2.50\n", "Id,Net\nX1,997.50\n");

        Assert.Equal(RecordStatus.Pass, o.Status);
        Assert.Equal(997.50m, o.LeftValue);
        Assert.Equal(997.50m, o.RightValue);
        Assert.Equal(0m, o.Difference);
        Assert.Equal("X1", o.Key);
        Assert.Equal(1, o.LeftRow);
        Assert.Equal(1, o.RightRow);
        Assert.Equal("", o.Reason);
    }

    [Fact]
    public void Mismatch_Fails_WithValuesDifferenceAndReason()
    {
        var rule = Rule("a.Gross - a.Fee", ComparisonOperator.Equal, "b.Net");
        var o = Single(rule, "Id,Gross,Fee\nX1,1000.00,2.50\n", "Id,Net\nX1,990.00\n");

        Assert.Equal(RecordStatus.Fail, o.Status);
        Assert.Equal(7.50m, o.Difference);
        Assert.Contains("997.50", o.Reason);
        Assert.Contains("990.00", o.Reason);
        Assert.Contains("exceeds tolerance", o.Reason);
    }

    [Fact]
    public void ColumnNames_ComeFromTheFiles_NotFromCode()
    {
        // Completely different schema from the sample data: a bank statement against an AP ledger.
        var rule = Rule("bank.[Debit Amt] + bank.[Bank Charges]", ComparisonOperator.Equal, "ap.[Invoice Total]",
            a: "bank", b: "ap", keyA: "Narrative Ref", keyB: "Invoice No");
        var bank = "Narrative Ref,Debit Amt,Bank Charges\nINV-1,100.00,1.50\nINV-2,50.00,0.00\n";
        var ap = "Invoice No,Invoice Total\nINV-1,101.50\nINV-2,55.00\n";

        var outcomes = RuleEvaluator.Evaluate(rule, T(bank), T(ap));

        Assert.Equal([RecordStatus.Pass, RecordStatus.Fail], outcomes.Select(o => o.Status));
    }

    // ---- tolerance ----

    [Theory]
    [InlineData("100.00", "100.04", 0.05, true)]
    [InlineData("100.00", "100.05", 0.05, true)]    // boundary is inclusive
    [InlineData("100.00", "100.06", 0.05, false)]
    [InlineData("100.00", "99.94", 0.05, false)]    // either direction
    [InlineData("100.00", "100.00", 0, true)]
    [InlineData("100.00", "100.01", 0, false)]
    public void AbsoluteTolerance(string l, string r, double tolerance, bool pass)
    {
        var rule = Rule("a.V", ComparisonOperator.Equal, "b.V", (decimal)tolerance);
        Assert.Equal(pass ? RecordStatus.Pass : RecordStatus.Fail, Single(rule, $"Id,V\nk,{l}\n", $"Id,V\nk,{r}\n").Status);
    }

    [Theory]
    [InlineData("1000", "1009", 1, true)]     // 0.9% of 1009 is within 1%? allowed = 10.09, diff 9
    [InlineData("1000", "1011", 1, false)]    // allowed = 10.11, diff 11
    [InlineData("0", "0", 5, true)]
    [InlineData("1", "0", 5, false)]          // right is zero: any difference fails
    public void PercentTolerance_IsRelativeToTheRightValue(string l, string r, double pct, bool pass)
    {
        var rule = Rule("a.V", ComparisonOperator.Equal, "b.V", (decimal)pct, ToleranceMode.Percent);
        Assert.Equal(pass ? RecordStatus.Pass : RecordStatus.Fail, Single(rule, $"Id,V\nk,{l}\n", $"Id,V\nk,{r}\n").Status);
    }

    [Fact]
    public void PassWithinTolerance_SaysSo()
    {
        var rule = Rule("a.V", ComparisonOperator.Equal, "b.V", 0.05m);
        var o = Single(rule, "Id,V\nk,100.00\n", "Id,V\nk,100.01\n");
        Assert.Equal(RecordStatus.Pass, o.Status);
        Assert.Contains("Within tolerance", o.Reason);
    }

    // ---- operators ----

    [Theory]
    [InlineData(ComparisonOperator.NotEqual, "1", "2", true)]
    [InlineData(ComparisonOperator.NotEqual, "2", "2", false)]
    [InlineData(ComparisonOperator.LessThan, "1", "2", true)]
    [InlineData(ComparisonOperator.LessThan, "2", "2", false)]
    [InlineData(ComparisonOperator.LessThanOrEqual, "2", "2", true)]
    [InlineData(ComparisonOperator.LessThanOrEqual, "3", "2", false)]
    [InlineData(ComparisonOperator.GreaterThan, "3", "2", true)]
    [InlineData(ComparisonOperator.GreaterThan, "2", "2", false)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, "2", "2", true)]
    [InlineData(ComparisonOperator.GreaterThanOrEqual, "1", "2", false)]
    public void Operators(ComparisonOperator op, string l, string r, bool pass)
    {
        var rule = Rule("a.V", op, "b.V");
        Assert.Equal(pass ? RecordStatus.Pass : RecordStatus.Fail, Single(rule, $"Id,V\nk,{l}\n", $"Id,V\nk,{r}\n").Status);
    }

    [Fact]
    public void FailedInequality_ExplainsItself()
    {
        var o = Single(Rule("a.V", ComparisonOperator.GreaterThan, "b.V"), "Id,V\nk,1\n", "Id,V\nk,2\n");
        Assert.Contains("not greater than", o.Reason);
    }

    // ---- joining ----

    [Fact]
    public void KeysAreTrimmedAndCaseInsensitive()
    {
        var o = Single(Rule("a.V", ComparisonOperator.Equal, "b.V"), "Id,V\n  abc ,1\n", "Id,V\nABC,1\n");
        Assert.Equal(RecordStatus.Pass, o.Status);
    }

    [Fact]
    public void KeyColumnsMayHaveDifferentNamesInEachFile()
    {
        var rule = Rule("a.V", ComparisonOperator.Equal, "b.V", keyA: "TradeId", keyB: "ClientTradeRef");
        var o = Single(rule, "TradeId,V\nT1,5\n", "ClientTradeRef,V\nT1,5\n");
        Assert.Equal(RecordStatus.Pass, o.Status);
    }

    [Fact]
    public void RecordMissingFromTheRightFile_Fails()
    {
        var o = Assert.Single(RuleEvaluator.Evaluate(Rule("a.V", ComparisonOperator.Equal, "b.V"),
            T("Id,V\nk,1\n"), T("Id,V\n")));

        Assert.Equal(RecordStatus.Fail, o.Status);
        Assert.Equal("k", o.Key);
        Assert.Equal(1, o.LeftRow);
        Assert.Null(o.RightRow);
        Assert.Contains("right file", o.Reason);
    }

    [Fact]
    public void RecordOnlyInTheRightFile_IsReported()
    {
        var o = Assert.Single(RuleEvaluator.Evaluate(Rule("a.V", ComparisonOperator.Equal, "b.V"),
            T("Id,V\n"), T("Id,V\nz,1\n")));

        Assert.Equal(RecordStatus.Fail, o.Status);
        Assert.Equal("z", o.Key);
        Assert.Null(o.LeftRow);
        Assert.Equal(1, o.RightRow);
        Assert.Contains("left file", o.Reason);
    }

    [Fact]
    public void DuplicateKeyInTheRightFile_FailsInsteadOfPickingOne()
    {
        var o = Assert.Single(RuleEvaluator.Evaluate(Rule("a.V", ComparisonOperator.Equal, "b.V"),
            T("Id,V\nk,1\n"), T("Id,V\nk,1\nk,1\n")));

        Assert.Equal(RecordStatus.Fail, o.Status);
        Assert.Contains("2 times in the right file", o.Reason);
        Assert.Contains("rows 1, 2", o.Reason);
    }

    [Fact]
    public void DuplicateKeyInTheLeftFile_FailsEveryDuplicateRow()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule("a.V", ComparisonOperator.Equal, "b.V"),
            T("Id,V\nk,1\nk,1\nm,1\n"), T("Id,V\nk,1\nm,1\n"));

        Assert.Equal([RecordStatus.Fail, RecordStatus.Fail, RecordStatus.Pass], outcomes.Select(o => o.Status));
        Assert.Contains("left file", outcomes[0].Reason);
    }

    [Fact]
    public void BlankKeys_AreReportedOnBothSides()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule("a.V", ComparisonOperator.Equal, "b.V"),
            T("Id,V\n ,1\n"), T("Id,V\n,1\n"));

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(RecordStatus.Fail, o.Status));
        Assert.All(outcomes, o => Assert.Contains("empty", o.Reason));
    }

    [Fact]
    public void ResultOrder_IsLeftFileOrder_ThenRightOnlyRecords()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule("a.V", ComparisonOperator.Equal, "b.V"),
            T("Id,V\nc,1\na,1\n"), T("Id,V\nz,1\na,1\nc,1\n"));

        Assert.Equal(["c", "a", "z"], outcomes.Select(o => o.Key));
    }

    // ---- bad data does not crash the run ----

    [Fact]
    public void NonNumericCell_FailsThatRecordOnly_AndNamesTheColumn()
    {
        var outcomes = RuleEvaluator.Evaluate(Rule("a.V", ComparisonOperator.Equal, "b.V"),
            T("Id,V\nk1,abc\nk2,5\n"), T("Id,V\nk1,1\nk2,5\n"));

        Assert.Equal(RecordStatus.Fail, outcomes[0].Status);
        Assert.Contains("a.V is not a number ('abc')", outcomes[0].Reason);
        Assert.Null(outcomes[0].LeftValue);
        Assert.Equal(RecordStatus.Pass, outcomes[1].Status);
    }

    [Fact]
    public void EmptyCell_Fails_WithAClearReason()
    {
        var o = Single(Rule("a.V", ComparisonOperator.Equal, "b.V"), "Id,V\nk,\n", "Id,V\nk,1\n");
        Assert.Equal(RecordStatus.Fail, o.Status);
        Assert.Contains("a.V is empty", o.Reason);
    }

    [Fact]
    public void DivisionByZeroInOneRecord_FailsThatRecordOnly()
    {
        var rule = Rule("a.Num / a.Den", ComparisonOperator.Equal, "b.V");
        var outcomes = RuleEvaluator.Evaluate(rule,
            T("Id,Num,Den\nk1,10,0\nk2,10,2\n"), T("Id,V\nk1,1\nk2,5\n"));

        Assert.Contains("Division by zero", outcomes[0].Reason);
        Assert.Equal(RecordStatus.Pass, outcomes[1].Status);
    }

    [Fact]
    public void ThousandsSeparators_AreAccepted()
    {
        var o = Single(Rule("a.V", ComparisonOperator.Equal, "b.V"), "Id,V\nk,\"1,234.50\"\n", "Id,V\nk,1234.50\n");
        Assert.Equal(RecordStatus.Pass, o.Status);
    }

    // ---- rule vs files problems are loud, not silent ----

    [Fact]
    public void UnknownColumn_ThrowsWithTheAvailableColumns()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RuleEvaluator.Evaluate(
            Rule("a.Nope", ComparisonOperator.Equal, "b.V"), T("Id,V\nk,1\n"), T("Id,V\nk,1\n")));
        Assert.Contains("no column 'Nope'", ex.Message);
        Assert.Contains("Id, V", ex.Message);
    }

    [Fact]
    public void UnknownJoinKeyColumn_Throws() =>
        Assert.Contains("join key", Assert.Throws<InvalidOperationException>(() => RuleEvaluator.Evaluate(
            Rule("a.V", ComparisonOperator.Equal, "b.V", keyA: "Nope"), T("Id,V\nk,1\n"), T("Id,V\nk,1\n"))).Message);

    [Fact]
    public void ReferenceToAnAliasThatIsNotInTheRule_Throws() =>
        Assert.Contains("unknown file alias", Assert.Throws<InvalidOperationException>(() => RuleEvaluator.Evaluate(
            Rule("c.V", ComparisonOperator.Equal, "b.V"), T("Id,V\nk,1\n"), T("Id,V\nk,1\n"))).Message);

    // ---- within one file ----

    [Fact]
    public void SingleFileRule_ComparesColumnsOfTheSameRow()
    {
        // "column1 of file 1 minus column3 of file 1 equals column2 of file 1"
        var rule = Rule("a.Gross - a.Fee", ComparisonOperator.Equal, "a.Net", b: "a", keyA: "Id", keyB: null);
        var table = T("Id,Gross,Fee,Net\nr1,100,1,99\nr2,100,1,98\n");

        var outcomes = RuleEvaluator.Evaluate(rule, table, table);

        Assert.Equal([RecordStatus.Pass, RecordStatus.Fail], outcomes.Select(o => o.Status));
        Assert.Equal(["r1", "r2"], outcomes.Select(o => o.Key));
        Assert.Equal([1, 2], outcomes.Select(o => o.LeftRow!.Value));
        Assert.All(outcomes, o => Assert.Null(o.RightRow));
    }

    [Fact]
    public void SingleFileRuleWithoutKey_UsesRowNumbers()
    {
        var rule = Rule("a.X", ComparisonOperator.Equal, "a.Y", b: "a", keyA: null, keyB: null);
        var table = T("X,Y\n1,1\n2,3\n");

        Assert.Equal(["1", "2"], RuleEvaluator.Evaluate(rule, table, table).Select(o => o.Key));
    }

    [Fact]
    public void SingleFileRule_ReportsDuplicateKeysAsSeparateRecords()
    {
        // Within-file checks do not join, so repeated keys are simply separate rows.
        var rule = Rule("a.X", ComparisonOperator.Equal, "a.Y", b: "a", keyA: "Id", keyB: null);
        var table = T("Id,X,Y\nk,1,1\nk,1,2\n");

        Assert.Equal([RecordStatus.Pass, RecordStatus.Fail], RuleEvaluator.Evaluate(rule, table, table).Select(o => o.Status));
    }

    [Fact]
    public void Evaluation_IsRepeatable()
    {
        var rule = Rule("a.V", ComparisonOperator.Equal, "b.V");
        var a = T("Id,V\nk1,1\nk2,2\nk3,x\n");
        var b = T("Id,V\nk1,1\nk2,3\nk4,1\n");

        Assert.Equal(RuleEvaluator.Evaluate(rule, a, b), RuleEvaluator.Evaluate(rule, a, b));
    }
}
