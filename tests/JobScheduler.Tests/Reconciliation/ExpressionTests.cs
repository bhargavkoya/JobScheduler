using JobScheduler.Application.Auth;
using JobScheduler.Application.Reconciliation;

namespace JobScheduler.Tests.Reconciliation;

public class ExpressionTests
{
    private static decimal Eval(string text, Dictionary<string, decimal>? values = null)
    {
        values ??= new();
        return Expression.Parse(text).Evaluate(r => values[$"{r.Alias}.{r.Column}"]);
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 - 4 - 3", 3)]          // left associative
    [InlineData("100 / 10 / 5", 2)]        // left associative
    [InlineData("-5 + 2", -3)]
    [InlineData("2 * -3", -6)]
    [InlineData("- -4", 4)]
    [InlineData("+7", 7)]
    [InlineData(".5 + 0.25", 0.75)]
    [InlineData("  1+1  ", 2)]
    public void Arithmetic_FollowsPrecedence(string text, double expected) =>
        Assert.Equal((decimal)expected, Eval(text));

    [Fact]
    public void ColumnReferences_AreResolvedThroughTheCallback()
    {
        var v = new Dictionary<string, decimal> { ["a.Gross"] = 1000m, ["a.Fee"] = 2.5m, ["b.Net"] = 997.5m };
        Assert.Equal(0m, Eval("a.Gross - a.Fee - b.Net", v));
    }

    [Fact]
    public void BracketedColumnNames_AllowSpacesAndSymbols()
    {
        var e = Expression.Parse("file1.[Net Amount (INR)] * 2");
        var r = Assert.Single(e.References);
        Assert.Equal(new ColumnRef("file1", "Net Amount (INR)"), r);
    }

    [Fact]
    public void References_AreDistinctAndCaseInsensitive()
    {
        var e = Expression.Parse("a.X + A.x + a.Y + b.X");
        Assert.Equal(3, e.References.Count);
    }

    [Fact]
    public void DecimalArithmetic_IsExactForMoney() =>
        Assert.Equal(0.3m, Eval("0.1 + 0.2"));

    [Fact]
    public void DivisionByZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => Eval("1 / (2 - 2)"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1 +")]
    [InlineData("(1 + 2")]
    [InlineData("1 + 2)")]
    [InlineData("1 2")]
    [InlineData("amount")]               // missing alias.column form
    [InlineData("a.")]
    [InlineData("a.[Open")]
    [InlineData("1.2.3")]
    [InlineData("1 $ 2")]
    [InlineData("a.X ** 2")]
    public void MalformedExpressions_AreRejectedWithValidationErrors(string text) =>
        Assert.Throws<ValidationException>(() => Expression.Parse(text));

    [Fact]
    public void ErrorMessage_ShowsTheExpressionAndHowToFixIt()
    {
        var ex = Assert.Throws<ValidationException>(() => Expression.Parse("amount + 1"));
        Assert.Contains("amount + 1", ex.Message);
        Assert.Contains("alias", ex.Message);
    }

    [Fact]
    public void ExpressionText_IsNeverExecutedAsCode()
    {
        // Anything that is not numbers, column references and + - * / ( ) is a validation error.
        Assert.Throws<ValidationException>(() => Expression.Parse("System.Environment.Exit(1)"));
        Assert.Throws<ValidationException>(() => Expression.Parse("a.X; DROP TABLE Users"));
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData(" 1,234.50 ", 1234.5)]
    [InlineData("1,23,456.00", 123456)]    // Indian digit grouping
    [InlineData("-0.01", -0.01)]
    [InlineData("+5", 5)]
    public void CellNumber_ParsesPlainDecimals(string raw, double expected) =>
        Assert.Equal((decimal)expected, CellNumber.Parse(raw, "x.Y"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("$12")]
    [InlineData("(12.00)")]
    [InlineData("1e5")]
    public void CellNumber_RejectsNonNumbers_NamingTheColumn(string? raw)
    {
        var ex = Assert.Throws<CellValueException>(() => CellNumber.Parse(raw, "internal.Amount"));
        Assert.StartsWith("internal.Amount", ex.Message);
    }
}
