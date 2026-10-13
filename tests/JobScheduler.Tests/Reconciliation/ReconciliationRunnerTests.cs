using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JobScheduler.Application.Reconciliation;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Runs;
using Moq;

namespace JobScheduler.Tests.Reconciliation;

public class ReconciliationRunnerTests
{
    private readonly Mock<IReconciliationStore> _store = new();
    private readonly Job _job = new() { Name = "recon", Template = new JobTemplate { IsFileReconciliation = true } };
    private readonly JobRun _run = new() { IdempotencyKey = "k" };
    private readonly List<IReadOnlyList<ReconciliationResult>> _stored = [];

    public ReconciliationRunnerTests()
    {
        _run.JobId = _job.Id;
        // Like the real store: no files / no rules is an empty list, never null.
        _store.Setup(s => s.ListActiveFilesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_files);
        _store.Setup(s => s.ListRulesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_rules);
        _store.Setup(s => s.ReplaceResultsAsync(_run.Id, It.IsAny<IReadOnlyList<ReconciliationResult>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyList<ReconciliationResult>, CancellationToken>((_, r, _) => _stored.Add(r))
            .Returns(Task.CompletedTask);
    }

    private ReconciliationRunner Sut() => new(_store.Object);

    private InputFile AddFile(string alias, string csv)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        var file = new InputFile
        {
            Alias = alias, FileName = alias + ".csv", JobId = _job.Id, RowCount = CsvTable.Parse(bytes).Rows.Count,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        };
        _store.Setup(s => s.GetContentAsync(file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bytes);
        _files.Add(file);
        _store.Setup(s => s.ListActiveFilesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_files);
        return file;
    }

    private readonly List<InputFile> _files = [];

    private ReconciliationRule AddRule(string name, string left, string op, string right, int position = 1, decimal tol = 0)
    {
        var rule = new ReconciliationRule
        {
            JobId = _job.Id, Position = position, Name = name, LeftAlias = "a", RightAlias = "b",
            LeftKeyColumn = "Id", RightKeyColumn = "Id", LeftExpression = left, RightExpression = right,
            Operator = op == "==" ? ComparisonOperator.Equal : ComparisonOperator.GreaterThan, Tolerance = tol
        };
        _rules.Add(rule);
        _store.Setup(s => s.ListRulesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_rules);
        return rule;
    }

    private readonly List<ReconciliationRule> _rules = [];

    private const string FileA = "Id,Gross,Fee\nT1,100,1\nT2,200,2\nT3,300,3\n";
    private const string FileB = "Id,Net\nT1,99\nT2,190\nT4,5\n";

    // ---- snapshot ----

    [Fact]
    public async Task Snapshot_PinsTheCurrentFiles_InAliasOrder()
    {
        var b = AddFile("b", FileB);
        var a = AddFile("a", FileA);
        AddRule("R", "a.Gross", "==", "b.Net");

        var snapshot = JsonSerializer.Deserialize<InputSnapshot>(await Sut().SnapshotInputsAsync(_job, default))!;

        Assert.Equal(["a", "b"], snapshot.Files.Select(f => f.Alias));
        Assert.Equal(a.Id, snapshot.Files[0].FileId);
        Assert.Equal(a.Sha256, snapshot.Files[0].Sha256);
        Assert.Equal(3, snapshot.Files[0].Rows);
        Assert.Equal(b.Id, snapshot.Files[1].FileId);
    }

    [Fact]
    public async Task Snapshot_WithoutFiles_Throws() =>
        Assert.Contains("no input files", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().SnapshotInputsAsync(_job, default))).Message);

    [Fact]
    public async Task Snapshot_WithoutRules_Throws()
    {
        AddFile("a", FileA);
        Assert.Contains("no reconciliation rules", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().SnapshotInputsAsync(_job, default))).Message);
    }

    // ---- run ----

    private async Task<(CalculationResult Result, string Snapshot)> RunAsync()
    {
        var snapshot = await Sut().SnapshotInputsAsync(_job, default);
        return (await Sut().RunAsync(_job, _run, snapshot, default), snapshot);
    }

    [Fact]
    public async Task Run_StoresOneResultPerRecordPerRule_AndSummarises()
    {
        AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("Net matches", "a.Gross - a.Fee", "==", "b.Net");

        var (result, _) = await RunAsync();

        var stored = Assert.Single(_stored);
        // T1 pass, T2 fail (198 vs 190), T3 missing in B, then T4 only in B.
        Assert.Equal(["T1", "T2", "T3", "T4"], stored.Select(r => r.RecordKey));
        Assert.Equal([RecordStatus.Pass, RecordStatus.Fail, RecordStatus.Fail, RecordStatus.Fail], stored.Select(r => r.Status));
        Assert.All(stored, r => Assert.Equal(_run.Id, r.RunId));
        Assert.All(stored, r => Assert.Equal(_rules[0].Id, r.RuleId));
        Assert.Equal("Net matches", stored[0].RuleName);

        Assert.Contains("1 of 4 record checks passed, 3 failed", result.Summary);
        Assert.Contains("Net matches: 1 of 4 records passed, 3 failed", result.Summary);
        Assert.Contains("a.Gross - a.Fee == b.Net", result.Summary);
        Assert.Contains("Net matches / T2", result.Summary);
        Assert.Equal(3m, result.Values["Net matches"]);
    }

    [Fact]
    public async Task Run_WithSeveralRules_KeepsThemApart()
    {
        AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("first", "a.Gross - a.Fee", "==", "b.Net", position: 1);
        AddRule("second", "a.Gross", ">", "b.Net", position: 2);

        var (result, _) = await RunAsync();

        var stored = Assert.Single(_stored);
        Assert.Equal(8, stored.Count);
        Assert.Equal(4, stored.Count(r => r.RuleName == "first"));
        Assert.Equal(4, stored.Count(r => r.RuleName == "second"));
        Assert.Equal(["first", "second"], result.Values.Keys);
        Assert.Contains("across 2 rule(s)", result.Summary);
    }

    [Fact]
    public async Task Run_ExecutesRulesInPositionOrder()
    {
        AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("later", "a.Gross", "==", "b.Net", position: 2);
        AddRule("earlier", "a.Gross", "==", "b.Net", position: 1);

        var (result, _) = await RunAsync();

        Assert.Equal(["earlier", "later"], result.Values.Keys);
    }

    [Fact]
    public async Task Run_ARerun_ReplacesResultsForTheSameRun_AndGivesTheSameAnswer()
    {
        AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("R", "a.Gross - a.Fee", "==", "b.Net");
        var snapshot = await Sut().SnapshotInputsAsync(_job, default);

        var first = await Sut().RunAsync(_job, _run, snapshot, default);
        var second = await Sut().RunAsync(_job, _run, snapshot, default);

        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(2, _stored.Count);
        Assert.Equal(_stored[0].Select(r => (r.RecordKey, r.Status, r.Difference)),
            _stored[1].Select(r => (r.RecordKey, r.Status, r.Difference)));
        // Both writes go through the replace operation for this run id, which swaps rather than appends.
        _store.Verify(s => s.ReplaceResultsAsync(_run.Id, It.IsAny<IReadOnlyList<ReconciliationResult>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Run_UsesThePinnedFilesEvenIfTheJobsFilesChangeAfterwards()
    {
        var a = AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("R", "a.Gross - a.Fee", "==", "b.Net");
        var snapshot = await Sut().SnapshotInputsAsync(_job, default);

        // A newer file takes over alias "a" after the snapshot was taken.
        var newer = new InputFile { Alias = "a", FileName = "a2.csv", JobId = _job.Id };
        _files.Remove(a);
        _files.Add(newer);

        await Sut().RunAsync(_job, _run, snapshot, default);

        _store.Verify(s => s.GetContentAsync(a.Id, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.GetContentAsync(newer.Id, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Run_RejectsAFileWhoseContentChangedSinceTheSnapshot()
    {
        var a = AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("R", "a.Gross", "==", "b.Net");
        var snapshot = await Sut().SnapshotInputsAsync(_job, default);
        _store.Setup(s => s.GetContentAsync(a.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("Id,Gross,Fee\nT1,999,1\n"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().RunAsync(_job, _run, snapshot, default));

        Assert.Contains("no longer matches", ex.Message);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Run_WhenStoredContentIsMissing_Throws()
    {
        var a = AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("R", "a.Gross", "==", "b.Net");
        var snapshot = await Sut().SnapshotInputsAsync(_job, default);
        _store.Setup(s => s.GetContentAsync(a.Id, It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

        Assert.Contains("missing", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sut().RunAsync(_job, _run, snapshot, default))).Message);
    }

    [Fact]
    public async Task Run_WhenARuleNoLongerFitsTheFile_FailsLoudlyAndStoresNothing()
    {
        AddFile("a", FileA);
        AddFile("b", FileB);
        AddRule("R", "a.Removed", "==", "b.Net");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync());

        Assert.Contains("no column 'Removed'", ex.Message);
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task Run_LongFailureExamplesAreCappedInTheSummary()
    {
        var rowsA = new StringBuilder("Id,V\n");
        var rowsB = new StringBuilder("Id,V\n");
        for (var i = 0; i < 100; i++) { rowsA.Append($"K{i},1\n"); rowsB.Append($"K{i},2\n"); }
        AddFile("a", rowsA.ToString());
        AddFile("b", rowsB.ToString());
        AddRule("R", "a.V", "==", "b.V");

        var (result, _) = await RunAsync();

        Assert.Contains("100 failed", result.Summary);
        Assert.Equal(10, result.Summary.Split('\n').Count(l => l.StartsWith("- R / K")));
        Assert.Single(_stored);
        Assert.Equal(100, _stored[0].Count);
    }

    // ---- the documented sample files, end to end through the runner ----

    [Fact]
    public async Task SampleFiles_RunThroughTheRunner_GivePerRecordResultsForEveryRule()
    {
        foreach (var (alias, name) in new[] { ("trades", "1_internal_trades.csv"), ("custodian", "2_custodian_settlements.csv"), ("ledger", "3_gl_cash_postings.csv") })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "samples", name));
            var file = new InputFile
            {
                Alias = alias, FileName = name, JobId = _job.Id, RowCount = CsvTable.Parse(bytes).Rows.Count,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            };
            _store.Setup(s => s.GetContentAsync(file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(bytes);
            _files.Add(file);
        }

        _store.Setup(s => s.ListActiveFilesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_files);
        _rules.Add(new ReconciliationRule
        {
            JobId = _job.Id, Position = 1, Name = "Cash", LeftAlias = "trades", RightAlias = "custodian",
            LeftKeyColumn = "TradeId", RightKeyColumn = "ClientTradeRef",
            LeftExpression = "trades.GrossCashAmount - trades.Commission - trades.Tax", RightExpression = "custodian.CashAmount"
        });
        _rules.Add(new ReconciliationRule
        {
            JobId = _job.Id, Position = 2, Name = "Ledger", LeftAlias = "custodian", RightAlias = "ledger",
            LeftKeyColumn = "ClientTradeRef", RightKeyColumn = "TradeId", Tolerance = 0.05m,
            LeftExpression = "custodian.CashAmount", RightExpression = "ledger.DebitAmount - ledger.CreditAmount"
        });
        _store.Setup(s => s.ListRulesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_rules);

        var (result, _) = await RunAsync();

        var stored = Assert.Single(_stored);
        Assert.Equal(62, stored.Count(r => r.RuleName == "Cash"));          // 60 trades + 2 custodian-only
        Assert.Equal(52, stored.Count(r => r.RuleName == "Cash" && r.Status == RecordStatus.Pass));
        Assert.Equal(10, result.Values["Cash"]);
        Assert.Equal(6 + 4 + 1 + 4, result.Values["Ledger"]);               // README: amounts, no posting, duplicate, ledger-only
        Assert.All(stored.Where(r => r.Status == RecordStatus.Fail), r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
    }
}
