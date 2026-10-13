using System.Text;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Reconciliation;

namespace JobScheduler.Tests.Reconciliation;

public class CsvTableTests
{
    internal static CsvTable Parse(string text) => CsvTable.Parse(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void ParsesHeadersAndRows()
    {
        var t = Parse("Id,Amount\r\nA1,10.50\r\nA2,-3\r\n");

        Assert.Equal(["Id", "Amount"], t.Headers);
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal(["A2", "-3"], t.Rows[1]);
    }

    [Fact]
    public void HeaderOnlyFile_HasNoRows()
    {
        var t = Parse("Id,Amount\n");
        Assert.Empty(t.Rows);
        Assert.Equal(2, t.Headers.Count);
    }

    [Fact]
    public void QuotedFields_KeepCommasQuotesAndLineBreaks()
    {
        var t = Parse("Id,Note\n1,\"Smith, John\"\n2,\"said \"\"hi\"\"\"\n3,\"line1\nline2\"\n");

        Assert.Equal("Smith, John", t.Rows[0][1]);
        Assert.Equal("said \"hi\"", t.Rows[1][1]);
        Assert.Equal("line1\nline2", t.Rows[2][1]);
    }

    [Fact]
    public void ByteOrderMark_IsIgnored()
    {
        var t = CsvTable.Parse([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Id,Amount\n1,2\n")]);
        Assert.Equal("Id", t.Headers[0]);
    }

    [Theory]
    [InlineData("Id;Amount\n1;2\n")]
    [InlineData("Id\tAmount\n1\t2\n")]
    [InlineData("Id|Amount\n1|2\n")]
    public void DelimiterIsDetectedFromTheHeader(string text)
    {
        var t = Parse(text);
        Assert.Equal(["Id", "Amount"], t.Headers);
        Assert.Equal(["1", "2"], t.Rows[0]);
    }

    [Fact]
    public void BlankLinesAndMissingFinalNewline_AreTolerated()
    {
        var t = Parse("Id,Amount\n\n1,2\n\n3,4");
        Assert.Equal(2, t.Rows.Count);
        Assert.Equal("4", t.Rows[1][1]);
    }

    [Fact]
    public void QuotedEmptyFieldAtEndOfFile_IsKept()
    {
        var t = Parse("Id\n\"\"");
        Assert.Single(t.Rows);
        Assert.Equal("", t.Rows[0][0]);
    }

    [Fact]
    public void ColumnLookup_IsCaseInsensitive()
    {
        var t = Parse("TradeId,Net Amount\n1,2\n");
        Assert.Equal(1, t.IndexOf("net amount"));
        Assert.Equal(0, t.IndexOf(" TRADEID "));
        Assert.Equal(-1, t.IndexOf("Missing"));
    }

    [Fact]
    public void EmptyFile_IsRejected() =>
        Assert.Contains("empty", Assert.Throws<ValidationException>(() => Parse("")).Message);

    [Fact]
    public void RowWithWrongFieldCount_NamesTheRow()
    {
        var ex = Assert.Throws<ValidationException>(() => Parse("A,B\n1,2\n3,4,5\n"));
        Assert.Contains("Row 2", ex.Message);
        Assert.Contains("3 fields", ex.Message);
    }

    [Fact]
    public void DuplicateHeaders_AreRejected_IgnoringCase() =>
        Assert.Contains("'amount'", Assert.Throws<ValidationException>(() => Parse("Amount,amount\n1,2\n")).Message,
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void BlankHeaderName_IsRejected() =>
        Assert.Contains("Column 2", Assert.Throws<ValidationException>(() => Parse("A,,C\n1,2,3\n")).Message);

    [Fact]
    public void UnclosedQuote_IsRejected() =>
        Assert.Contains("quote", Assert.Throws<ValidationException>(() => Parse("A,B\n1,\"oops\n")).Message);

    [Fact]
    public void TooManyRows_AreRejected()
    {
        var sb = new StringBuilder("A\n");
        for (var i = 0; i <= CsvTable.MaxRows; i++) sb.Append("1\n");
        Assert.Contains("more than", Assert.Throws<ValidationException>(() => Parse(sb.ToString())).Message);
    }

    [Fact]
    public void ExactlyMaxRows_IsAccepted()
    {
        var sb = new StringBuilder("A\n");
        for (var i = 0; i < CsvTable.MaxRows; i++) sb.Append("1\n");
        Assert.Equal(CsvTable.MaxRows, Parse(sb.ToString()).Rows.Count);
    }
}
