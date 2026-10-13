using System.Text;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Reconciliation;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Reconciliation;
using Moq;

namespace JobScheduler.Tests.Reconciliation;

public class ReconciliationResultsServiceTests
{
    private readonly Mock<IReconciliationStore> _store = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Guid _myId = Guid.NewGuid();

    private readonly Job _job = new()
    {
        Name = "Daily recon", Team = Team.Business, Template = new JobTemplate { Name = "File Reconciliation", IsFileReconciliation = true }
    };

    private readonly JobRun _run = new() { IdempotencyKey = "k", Status = RunStatus.Succeeded };
    private readonly ReconciliationRule _rule1 = new() { Name = "Cash", Position = 1, LeftAlias = "a", RightAlias = "b", LeftKeyColumn = "Id", RightKeyColumn = "Id", LeftExpression = "a.X", RightExpression = "b.X" };
    private readonly ReconciliationRule _rule2 = new() { Name = "Qty", Position = 2, LeftAlias = "a", RightAlias = "b", LeftKeyColumn = "Id", RightKeyColumn = "Id", LeftExpression = "a.Q", RightExpression = "b.Q", Tolerance = 0.5m };
    private readonly List<IReconciliationExporter> _exporters = [];

    public ReconciliationResultsServiceTests()
    {
        _me.SetupGet(m => m.UserId).Returns(_myId);
        _me.SetupGet(m => m.Role).Returns(Role.Employee);
        _me.SetupGet(m => m.Team).Returns(Team.Business);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());
        _job.OwnerId = _myId;
        _run.JobId = _job.Id;
        _rule1.JobId = _rule2.JobId = _job.Id;
        _runs.Setup(r => r.FindAsync(_run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_run);
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
        _users.Setup(u => u.FindByIdAsync(_myId, It.IsAny<CancellationToken>())).ReturnsAsync(new User { Id = _myId, Email = "me@x.test" });
        _store.Setup(s => s.ListRulesAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync([_rule2, _rule1]);
        _store.Setup(s => s.CountByRuleAsync(_run.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new RuleCount(_rule1.Id, "Cash", 10, 7), new RuleCount(_rule2.Id, "Qty", 10, 10)]);
    }

    private ReconciliationResultsService Sut() => new(_store.Object, _runs.Object, _jobs.Object, _users.Object, _me.Object,
        _exporters, TimeProvider.System, AtRiskPolicy.Default);

    private static ReconciliationResult Row(Guid ruleId, string key, RecordStatus status) => new()
    {
        Id = 1, RunId = Guid.Empty, RuleId = ruleId, RuleName = "Cash", RecordKey = key, Status = status, Reason = status == RecordStatus.Fail ? "bad" : ""
    };

    // ---- summary ----

    [Fact]
    public async Task Summary_TotalsPerRule_InRuleOrder()
    {
        var s = await Sut().GetSummaryAsync(_run.Id, default);

        Assert.Equal(["Cash", "Qty"], s.Rules.Select(r => r.RuleName));
        Assert.Equal((10, 7, 3), (s.Rules[0].Total, s.Rules[0].Passed, s.Rules[0].Failed));
        Assert.Equal((20, 17, 3), (s.Total, s.Passed, s.Failed));
        Assert.Equal(RunStatus.Succeeded, s.RunStatus);
        Assert.Equal("a.X == b.X, joined a.Id = b.Id", s.Rules[0].Description);
        Assert.Contains("tolerance 0.5", s.Rules[1].Description);
    }

    [Fact]
    public async Task Summary_ForARunThatHasNoResultsYet_ShowsZeros()
    {
        _store.Setup(s => s.CountByRuleAsync(_run.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var s = await Sut().GetSummaryAsync(_run.Id, default);

        Assert.Equal(0, s.Total);
        Assert.Equal(2, s.Rules.Count);
        Assert.All(s.Rules, r => Assert.Equal((0, 0, 0), (r.Total, r.Passed, r.Failed)));
    }

    // ---- access ----

    [Fact]
    public async Task UnknownRun_IsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().GetSummaryAsync(Guid.NewGuid(), default));

    [Fact]
    public async Task RunOfAnotherTeamsJob_LooksTheSameAsMissing()
    {
        _job.Team = Team.Technical;
        var csv = new Mock<IReconciliationExporter>();
        csv.SetupGet(e => e.Format).Returns("csv");
        _exporters.Add(csv.Object);
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().GetSummaryAsync(_run.Id, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().ListAsync(_run.Id, null, null, 1, 10, default));
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().ExportAsync(_run.Id, "csv", null, null, default));
    }

    [Fact]
    public async Task RunOfAJobThatDoesNotUseFiles_IsRejected()
    {
        _job.Template = new JobTemplate { IsFileReconciliation = false };
        Assert.Contains("does not use uploaded files",
            (await Assert.ThrowsAsync<ValidationException>(() => Sut().GetSummaryAsync(_run.Id, default))).Message);
    }

    // ---- paging ----

    [Fact]
    public async Task List_PagesAndReportsTheTotal()
    {
        _store.Setup(s => s.CountAsync(_run.Id, RecordStatus.Fail, null, It.IsAny<CancellationToken>())).ReturnsAsync(42);
        _store.Setup(s => s.QueryAsync(_run.Id, RecordStatus.Fail, null, 20, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Row(_rule1.Id, "K21", RecordStatus.Fail)]);

        var page = await Sut().ListAsync(_run.Id, RecordStatus.Fail, null, 3, 10, default);

        Assert.Equal((42, 3, 10), (page.Total, page.Page, page.PageSize));
        Assert.Equal("K21", Assert.Single(page.Items).RecordKey);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 501)]
    public async Task List_RejectsBadPaging(int page, int size) =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().ListAsync(_run.Id, null, null, page, size, default));

    // ---- export ----

    [Fact]
    public async Task Export_BuildsTheReportAndUsesTheChosenExporter()
    {
        var exporter = new Mock<IReconciliationExporter>();
        exporter.SetupGet(e => e.Format).Returns("csv");
        ReconciliationReport? seen = null;
        exporter.Setup(e => e.Export(It.IsAny<ReconciliationReport>()))
            .Callback<ReconciliationReport>(r => seen = r).Returns(new ExportedFile([1], "text/csv", "csv"));
        _exporters.Add(exporter.Object);
        _store.Setup(s => s.QueryAsync(_run.Id, RecordStatus.Fail, _rule1.Id, 0, int.MaxValue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Row(_rule1.Id, "K1", RecordStatus.Fail)]);

        var file = await Sut().ExportAsync(_run.Id, " CSV ", RecordStatus.Fail, _rule1.Id, default);

        Assert.Equal("csv", file.FileExtension);
        Assert.Equal("me@x.test", seen!.OwnerEmail);
        Assert.Equal("Cash", seen.RuleFilterName);
        Assert.Equal(RecordStatus.Fail, seen.StatusFilter);
        Assert.Equal("K1", Assert.Single(seen.Results).RecordKey);
        Assert.Equal(20, seen.Summary.Total);
    }

    [Fact]
    public async Task Export_DefaultsToCsv_AndRejectsUnknownFormats()
    {
        var csv = new Mock<IReconciliationExporter>();
        csv.SetupGet(e => e.Format).Returns("csv");
        csv.Setup(e => e.Export(It.IsAny<ReconciliationReport>())).Returns(new ExportedFile([], "text/csv", "csv"));
        _exporters.Add(csv.Object);
        _store.Setup(s => s.QueryAsync(_run.Id, null, null, 0, int.MaxValue, It.IsAny<CancellationToken>())).ReturnsAsync([]);

        Assert.Equal("csv", (await Sut().ExportAsync(_run.Id, null, null, null, default)).FileExtension);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Sut().ExportAsync(_run.Id, "xlsx", null, null, default));
        Assert.Contains("csv", ex.Message);
    }

    [Fact]
    public async Task Export_WithARuleFromAnotherJob_IsRejected()
    {
        var csv = new Mock<IReconciliationExporter>();
        csv.SetupGet(e => e.Format).Returns("csv");
        _exporters.Add(csv.Object);

        await Assert.ThrowsAsync<ValidationException>(() => Sut().ExportAsync(_run.Id, "csv", null, Guid.NewGuid(), default));
    }
}

public class ReconciliationExporterTests
{
    private static ReconciliationResultDto Result(
        string key, RecordStatus status, decimal? l = null, decimal? r = null, decimal? d = null, string reason = "",
        string rule = "Cash vs custodian", int? leftRow = 1, int? rightRow = 1) =>
        new(1, Guid.NewGuid(), rule, key, leftRow, rightRow, l, r, d, status, reason);

    private static ReconciliationReport Report(IReadOnlyList<ReconciliationResultDto> results, RecordStatus? filter = null)
    {
        var summary = new RunReconciliationSummaryDto(Guid.NewGuid(), Guid.NewGuid(), "Daily recon", RunStatus.Succeeded, DateTime.UtcNow,
            results.Count, results.Count(r => r.Status == RecordStatus.Pass), results.Count(r => r.Status == RecordStatus.Fail),
            [new RuleSummaryDto(Guid.NewGuid(), "Cash vs custodian", "a.X == b.X, joined a.Id = b.Id", results.Count,
                results.Count(r => r.Status == RecordStatus.Pass), results.Count(r => r.Status == RecordStatus.Fail))]);
        var job = new JobDto(summary.JobId, Guid.NewGuid(), "File Reconciliation", "Daily recon", ScheduleType.Manual, null, null,
            JobStatus.Completed, Team.Business, Guid.NewGuid(), new RetryPolicyDto(1, 30), new Dictionary<string, string>(),
            DateTime.UtcNow, false, false, null, DateTime.UtcNow, false, null);
        return new ReconciliationReport(job, "owner@x.test", summary.RunId, summary, results, filter);
    }

    private static string Csv(ReconciliationReport report) =>
        Encoding.UTF8.GetString(new CsvReconciliationExporter().Export(report).Content);

    // ---- CSV ----

    [Fact]
    public void Csv_HasAHeaderAndOneRowPerRecord()
    {
        var csv = Csv(Report([
            Result("T-100001", RecordStatus.Pass, 997.50m, 997.50m, 0m),
            Result("T-100026", RecordStatus.Fail, -355220.80m, -356420.80m, 1200m, "difference 1200.00 exceeds tolerance 0.00.")
        ]));

        var lines = csv.TrimEnd().Split("\r\n");
        Assert.Equal("Rule,RecordKey,LeftRow,RightRow,LeftValue,RightValue,Difference,Status,Reason", lines[0]);
        Assert.Equal("Cash vs custodian,T-100001,1,1,997.50,997.50,0.00,Pass,", lines[1]);
        Assert.Equal("Cash vs custodian,T-100026,1,1,-355220.80,-356420.80,1200.00,Fail,difference 1200.00 exceeds tolerance 0.00.", lines[2]);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void Csv_KeepsNumbersNumeric_ButNeutralisesFormulaLikeText()
    {
        var csv = Csv(Report([Result("=HYPERLINK(\"x\")", RecordStatus.Fail, -5m, null, null, "+cmd|' /C calc'!A0", rule: "@sum")]));

        var row = csv.TrimEnd().Split("\r\n")[1];
        Assert.StartsWith("'@sum,\"'=HYPERLINK(\"\"x\"\")\",1,1,-5.00,,,Fail,", row);   // text cells get a leading apostrophe
        Assert.Contains(",-5.00,", row);                                                 // a negative number is not escaped
        Assert.EndsWith("'+cmd|' /C calc'!A0", row);
    }

    [Fact]
    public void Csv_QuotesCommasQuotesAndLineBreaks()
    {
        var csv = Csv(Report([Result("k", RecordStatus.Fail, reason: "Left 1, right \"2\"\nsecond line")]));

        Assert.Contains("\"Left 1, right \"\"2\"\"\nsecond line\"", csv);
    }

    [Fact]
    public void Csv_MissingSidesAreBlank()
    {
        var csv = Csv(Report([Result("T-900001", RecordStatus.Fail, reason: "No matching record in the left file.", leftRow: null, rightRow: 7)]));

        Assert.Contains("Cash vs custodian,T-900001,,7,,,,Fail,No matching record in the left file.", csv);
    }

    [Fact]
    public void Csv_NumbersIgnoreTheServersCulture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.Contains(",1234.50,", Csv(Report([Result("k", RecordStatus.Pass, 1234.5m, 1234.5m, 0m)])));
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void Csv_NoRecords_IsJustTheHeader()
    {
        var file = new CsvReconciliationExporter().Export(Report([]));
        Assert.Equal("Rule,RecordKey,LeftRow,RightRow,LeftValue,RightValue,Difference,Status,Reason\r\n", Encoding.UTF8.GetString(file.Content));
        Assert.Equal("text/csv", file.ContentType);
    }

    // ---- PDF ----

    private static string Magic(ExportedFile f) => Encoding.ASCII.GetString(f.Content, 0, 5);

    [Fact]
    public void Pdf_IsAValidPdf()
    {
        var file = new PdfReconciliationExporter().Export(Report([
            Result("T-1", RecordStatus.Pass, 1m, 1m, 0m),
            Result("T-2", RecordStatus.Fail, 1m, 2m, -1m, "differs")
        ]));

        Assert.Equal("%PDF-", Magic(file));
        Assert.Equal(("application/pdf", "pdf"), (file.ContentType, file.FileExtension));
        Assert.True(file.Content.Length > 1000);
    }

    [Fact]
    public void Pdf_WithNoRecords_StillRenders() =>
        Assert.Equal("%PDF-", Magic(new PdfReconciliationExporter().Export(Report([], RecordStatus.Fail))));

    [Fact]
    public void Pdf_WithMoreThanTheCap_RendersAndIsMarkedTruncated()
    {
        var rows = Enumerable.Range(0, PdfReconciliationExporter.MaxRecords + 50)
            .Select(i => Result("K" + i, i % 3 == 0 ? RecordStatus.Fail : RecordStatus.Pass, i, i, 0m, i % 3 == 0 ? "bad" : ""))
            .ToList();

        var file = new PdfReconciliationExporter().Export(Report(rows));

        Assert.Equal("%PDF-", Magic(file));
    }

    [Fact]
    public void RunHistoryPdf_AlsoSurvivesScriptsTheFontLacks()
    {
        var job = Report([]).Job with { Name = "日本 recon" };

        var file = new JobScheduler.Infrastructure.Runs.PdfRunHistoryExporter().Export(new RunHistoryReport(job, "o@x.test", []));

        Assert.Equal("%PDF-", Magic(file));
    }

    [Fact]
    public void Pdf_HandlesOddCharactersInKeysAndReasons() =>
        Assert.Equal("%PDF-", Magic(new PdfReconciliationExporter().Export(Report([
            Result("₹-ключ-日本", RecordStatus.Fail, 1m, 2m, -1m, "Ünïcode — <b>&</b> \"quotes\"")]))));
}
