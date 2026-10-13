using System.Text.Json;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Reconciliation;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Reconciliation;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using Moq;

namespace JobScheduler.Tests.Reconciliation;

/// <summary>File jobs use the runner in the download and calculate steps; every other job is untouched.</summary>
public class PipelineStepWiringTests
{
    private readonly Mock<IFinanceAppClient> _finance = new();
    private readonly Mock<ICalculationEngine> _engine = new();
    private readonly Mock<IReconciliationRunner> _runner = new();

    private static Job JobOf(bool fileReconciliation) => new()
    {
        Name = "j", ConfigJson = "{}", Template = new JobTemplate { IsFileReconciliation = fileReconciliation }
    };

    private static StepContext Context(Job job, params (PipelineStep Step, string Output)[] prior) =>
        new(job, new JobRun { JobId = job.Id }, prior.ToDictionary(p => p.Step, p => p.Output));

    [Fact]
    public async Task Download_ForAFileJob_PinsTheFilesAndNeverCallsTheFinanceApp()
    {
        var job = JobOf(true);
        _runner.Setup(r => r.SnapshotInputsAsync(job, It.IsAny<CancellationToken>())).ReturnsAsync("{\"files\":[]}");

        var output = await new DownloadReportStep(_finance.Object, _runner.Object).ExecuteAsync(Context(job), default);

        Assert.Equal("{\"files\":[]}", output);
        _finance.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Download_ForAnOrdinaryJob_StillCallsTheFinanceApp()
    {
        _finance.Setup(f => f.TriggerReportAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("r1");
        _finance.Setup(f => f.DownloadReportAsync("r1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReportData("r1", [new ReportRow("A", 1m)]));

        await new DownloadReportStep(_finance.Object, _runner.Object).ExecuteAsync(Context(JobOf(false)), default);

        _finance.Verify(f => f.DownloadReportAsync("r1", It.IsAny<CancellationToken>()), Times.Once);
        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Calculate_ForAFileJob_RunsTheRunnerWithTheDownloadSnapshot()
    {
        var job = JobOf(true);
        var ctx = Context(job, (PipelineStep.DownloadReport, "SNAPSHOT"));
        _runner.Setup(r => r.RunAsync(job, ctx.Run, "SNAPSHOT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalculationResult("3 of 4 passed", new Dictionary<string, decimal> { ["R"] = 1 }));

        var output = await new CalculateStep(_engine.Object, _runner.Object).ExecuteAsync(ctx, default);

        // The email step reads Summary back out of this JSON.
        Assert.Equal("3 of 4 passed", JsonSerializer.Deserialize<CalculationResult>(output)!.Summary);
        _engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Calculate_ForAnOrdinaryJob_StillUsesTheRulesEngine()
    {
        var job = JobOf(false);
        var report = new ReportData("r1", [new ReportRow("A", 1m)]);
        _engine.Setup(e => e.CalculateAsync(job.Id, It.IsAny<ReportData>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalculationResult("ok", new Dictionary<string, decimal>()));

        await new CalculateStep(_engine.Object, _runner.Object)
            .ExecuteAsync(Context(job, (PipelineStep.DownloadReport, JsonSerializer.Serialize(report))), default);

        _engine.Verify(e => e.CalculateAsync(job.Id, It.IsAny<ReportData>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Calculate_BeforeDownload_StillThrows() =>
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CalculateStep(_engine.Object, _runner.Object).ExecuteAsync(Context(JobOf(true)), default));
}

public class FileJobCreationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<ITemplateStore> _templates = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IReconciliationSetupService> _recon = new();
    private readonly Guid _myId = Guid.NewGuid();

    private readonly JobTemplate _fileTemplate = new()
    {
        Name = "File Reconciliation", IsApproved = true, IsFileReconciliation = true,
        SupportedScheduleTypes = [ScheduleType.Manual, ScheduleType.TriggerBased],
        Fields = [new TemplateField { Name = "recipients", Label = "Result recipient", Type = FieldType.Email, Required = true }]
    };

    private readonly JobTemplate _plainTemplate = new()
    {
        Name = "Plain", IsApproved = true, SupportedScheduleTypes = [ScheduleType.Manual], Fields = []
    };

    private static readonly ReconciliationSetupDto Setup = new(
        [new AttachFileDto(Guid.NewGuid(), "a"), new AttachFileDto(Guid.NewGuid(), "b")],
        [new ReconciliationRuleDto(null, "R", "a", "b", "Id", "Id", "a.X", ComparisonOperator.Equal, "b.Y")]);

    public FileJobCreationTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _me.SetupGet(m => m.UserId).Returns(_myId);
        _me.SetupGet(m => m.Role).Returns(Role.Employee);
        _me.SetupGet(m => m.Team).Returns(Team.Business);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());
        _templates.Setup(t => t.FindAsync(_fileTemplate.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_fileTemplate);
        _templates.Setup(t => t.FindAsync(_plainTemplate.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_plainTemplate);
    }

    private JobService Sut() => new(_jobs.Object, _templates.Object, _me.Object, _clock.Object, _scheduler.Object,
        _users.Object, AtRiskPolicy.Default, _recon.Object);

    private CreateJobRequest FileRequest(ReconciliationSetupDto? setup) => new(
        _fileTemplate.Id, "Daily recon", ScheduleType.Manual, null,
        new Dictionary<string, string> { ["recipients"] = "a@b.com" }, null, Reconciliation: setup);

    [Fact]
    public async Task Create_AttachesFilesAndRules_BeforeTheSingleSave()
    {
        var calls = new List<string>();
        _recon.Setup(r => r.AttachAsync(It.IsAny<Job>(), Setup, It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("attach")).Returns(Task.CompletedTask);
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>())).Callback(() => calls.Add("add")).Returns(Task.CompletedTask);
        _jobs.Setup(j => j.SaveChangesAsync(It.IsAny<CancellationToken>())).Callback(() => calls.Add("save")).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(FileRequest(Setup), default);

        Assert.Equal(["attach", "add", "save"], calls);
        Assert.Equal("Daily recon", dto.Name);
    }

    [Fact]
    public async Task Create_PassesTheNewJobToTheSetupService()
    {
        Job? seen = null;
        _recon.Setup(r => r.AttachAsync(It.IsAny<Job>(), It.IsAny<ReconciliationSetupDto?>(), It.IsAny<CancellationToken>()))
            .Callback<Job, ReconciliationSetupDto?, CancellationToken>((j, _, _) => seen = j).Returns(Task.CompletedTask);
        Job? added = null;
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((j, _) => added = j).Returns(Task.CompletedTask);

        await Sut().CreateAsync(FileRequest(Setup), default);

        Assert.Same(added, seen);
        Assert.Equal(_myId, seen!.OwnerId);
    }

    [Fact]
    public async Task Create_WhenTheSetupIsInvalid_SavesNothing()
    {
        _recon.Setup(r => r.AttachAsync(It.IsAny<Job>(), It.IsAny<ReconciliationSetupDto?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException("bad rule"));

        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(FileRequest(Setup), default));

        _jobs.Verify(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()), Times.Never);
        _jobs.Verify(j => j.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_FromAFileTemplateWithoutASetup_AsksTheSetupServiceToJudge()
    {
        _recon.Setup(r => r.AttachAsync(It.IsAny<Job>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException("Upload at least one CSV file for this job."));

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(FileRequest(null), default));

        Assert.Contains("Upload at least one", ex.Message);
        _jobs.Verify(j => j.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_FromAnOrdinaryTemplate_RefusesAFileSetup()
    {
        var request = new CreateJobRequest(_plainTemplate.Id, "x", ScheduleType.Manual, null,
            new Dictionary<string, string>(), null, Reconciliation: Setup);

        var ex = await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(request, default));

        Assert.Contains("does not take uploaded files", ex.Message);
        _recon.VerifyNoOtherCalls();
        _jobs.Verify(j => j.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_FromAnOrdinaryTemplate_NeverTouchesTheSetupService()
    {
        var request = new CreateJobRequest(_plainTemplate.Id, "x", ScheduleType.Manual, null, new Dictionary<string, string>(), null);

        await Sut().CreateAsync(request, default);

        _recon.VerifyNoOtherCalls();
        _jobs.Verify(j => j.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TemplateList_TellsTheUiWhichTemplatesTakeFiles()
    {
        _templates.Setup(t => t.ListAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([_fileTemplate, _plainTemplate]);

        var list = await new TemplateService(_templates.Object).ListAsync(false, default);

        Assert.True(list.Single(t => t.Name == "File Reconciliation").IsFileReconciliation);
        Assert.False(list.Single(t => t.Name == "Plain").IsFileReconciliation);
    }
}
