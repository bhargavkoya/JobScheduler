using System.Text.Json;
using JobScheduler.Application.Approvals;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using Moq;

namespace JobScheduler.Tests.Runs;

public class JobRunServiceTests
{
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IRunOrchestrator> _orchestrator = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Guid _myId = Guid.NewGuid();
    private readonly Job _job;

    public JobRunServiceTests()
    {
        _me.SetupGet(m => m.UserId).Returns(_myId);
        _me.SetupGet(m => m.Role).Returns(Role.Employee);
        _me.SetupGet(m => m.Team).Returns(Team.Business);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());

        _job = new Job { Name = "j", ScheduleType = ScheduleType.Manual, Team = Team.Business, OwnerId = _myId };
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
        _orchestrator.Setup(o => o.EnqueueManualAsync(_job.Id, It.IsAny<string?>(), _myId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobRun { JobId = _job.Id });
        _orchestrator.Setup(o => o.RetryAsync(_job.Id, _myId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobRun { JobId = _job.Id });
    }

    private JobRunService Sut() => new(_jobs.Object, _runs.Object, _orchestrator.Object, _me.Object);

    [Fact]
    public async Task RunNow_ByOwner_PassesIdempotencyKeyAndUserToOrchestrator()
    {
        await Sut().RunNowAsync(_job.Id, "key-1", default);

        _orchestrator.Verify(o => o.EnqueueManualAsync(_job.Id, "key-1", _myId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ScheduleType.Fixed)]
    [InlineData(ScheduleType.Recurrent)]
    public async Task RunNow_NonManualJob_Rejected(ScheduleType type)
    {
        _job.ScheduleType = type;

        await Assert.ThrowsAsync<ValidationException>(() => Sut().RunNowAsync(_job.Id, null, default));
    }

    [Fact]
    public async Task RunNow_ByNonOwnerEmployee_Forbidden()
    {
        _job.OwnerId = Guid.NewGuid();

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().RunNowAsync(_job.Id, null, default));
    }

    [Fact]
    public async Task RunNow_ByAdmin_Allowed()
    {
        _me.SetupGet(m => m.Role).Returns(Role.Admin);
        _job.OwnerId = Guid.NewGuid();

        await Sut().RunNowAsync(_job.Id, null, default);

        _orchestrator.Verify(o => o.EnqueueManualAsync(_job.Id, null, _myId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunNow_JobInInvisibleTeam_NotFound()
    {
        _job.Team = Team.Technical;

        await Assert.ThrowsAsync<NotFoundException>(() => Sut().RunNowAsync(_job.Id, null, default));
    }

    [Fact]
    public async Task Retry_WithoutRetryPermission_Forbidden()
    {
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().RetryAsync(_job.Id, default));

        _orchestrator.Verify(o => o.RetryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Retry_WithRetryPermission_Allowed_EvenForNonOwner()
    {
        _me.SetupGet(m => m.Permissions).Returns([Permissions.RetryJobs]);
        _job.OwnerId = Guid.NewGuid();

        await Sut().RetryAsync(_job.Id, default);

        _orchestrator.Verify(o => o.RetryAsync(_job.Id, _myId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Retry_ByAdmin_Allowed()
    {
        _me.SetupGet(m => m.Role).Returns(Role.Admin);

        await Sut().RetryAsync(_job.Id, default);

        _orchestrator.Verify(o => o.RetryAsync(_job.Id, _myId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListRuns_ReturnsRunsWithOrderedSteps()
    {
        var run = new JobRun { JobId = _job.Id };
        run.Steps.Add(new JobRunStep { Step = PipelineStep.Calculate, StartedAtUtc = new DateTime(2026, 1, 1, 0, 0, 2) });
        run.Steps.Add(new JobRunStep { Step = PipelineStep.DownloadReport, StartedAtUtc = new DateTime(2026, 1, 1, 0, 0, 1) });
        _runs.Setup(r => r.ListForJobAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync([run]);

        var result = await Sut().ListRunsAsync(_job.Id, default);

        Assert.Equal([PipelineStep.DownloadReport, PipelineStep.Calculate], result.Single().Steps.Select(s => s.Step));
    }

    [Fact]
    public async Task ListRuns_InvisibleJob_NotFound()
    {
        _job.Team = Team.Technical;

        await Assert.ThrowsAsync<NotFoundException>(() => Sut().ListRunsAsync(_job.Id, default));
    }
}

public class StartupRecoveryTests
{
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<IApprovalFollowUp> _followUp = new();

    public StartupRecoveryTests()
    {
        _jobs.Setup(j => j.ListNeedingManualActionAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _jobs.Setup(j => j.ListActiveRecurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    [Fact]
    public async Task Recover_ReregistersCronTriggers_ForRecurrentJobs()
    {
        var daily = new Job { ScheduleType = ScheduleType.Recurrent, RecurrenceCron = "0 0 9 * * ?" };
        _jobs.Setup(j => j.ListScheduledFixedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _jobs.Setup(j => j.ListActiveRecurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync([daily]);
        _runs.Setup(r => r.ListUnfinishedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await Sut().RecoverAsync(default);

        Assert.Equal(1, result.JobsRescheduled);
        _scheduler.Verify(s => s.ScheduleRecurrentAsync(daily.Id, "0 0 9 * * ?", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Recover_RebuildsFollowUpTriggers_ForJobsWaitingOnApproval()
    {
        var waiting = new Job { Status = JobStatus.NeedsManualAction };
        _jobs.Setup(j => j.ListScheduledFixedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _jobs.Setup(j => j.ListNeedingManualActionAsync(It.IsAny<CancellationToken>())).ReturnsAsync([waiting]);
        _runs.Setup(r => r.ListUnfinishedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await Sut().RecoverAsync(default);

        _followUp.Verify(f => f.ScheduleAsync(waiting, It.IsAny<CancellationToken>()), Times.Once);
    }

    private StartupRecovery Sut() => new(_jobs.Object, _runs.Object, _scheduler.Object, _queue.Object, _followUp.Object);

    [Fact]
    public async Task Recover_ReschedulesScheduledFixedJobs_AndRequeuesUnfinishedRuns()
    {
        var late = new Job { RunAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) }; // already in the past
        var future = new Job { RunAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var pending = new JobRun { Status = RunStatus.Pending };
        var running = new JobRun { Status = RunStatus.Running };
        _jobs.Setup(j => j.ListScheduledFixedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([late, future]);
        _runs.Setup(r => r.ListUnfinishedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([pending, running]);

        var result = await Sut().RecoverAsync(default);

        Assert.Equal(new RecoveryResult(2, 2), result);
        _scheduler.Verify(s => s.ScheduleFixedAsync(late.Id, late.RunAtUtc!.Value, It.IsAny<CancellationToken>()), Times.Once);
        _scheduler.Verify(s => s.ScheduleFixedAsync(future.Id, future.RunAtUtc!.Value, It.IsAny<CancellationToken>()), Times.Once);
        _queue.Verify(q => q.EnqueueAsync(pending.Id, null, It.IsAny<CancellationToken>()), Times.Once);
        _queue.Verify(q => q.EnqueueAsync(running.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Recover_NothingToDo_DoesNothing()
    {
        _jobs.Setup(j => j.ListScheduledFixedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _runs.Setup(r => r.ListUnfinishedAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        var result = await Sut().RecoverAsync(default);

        Assert.Equal(new RecoveryResult(0, 0), result);
        _scheduler.VerifyNoOtherCalls();
        _queue.VerifyNoOtherCalls();
    }
}

public class PipelineStepTests
{
    private static Job JobWith(string configJson) => new()
    {
        Name = "Recon",
        ConfigJson = configJson,
        Template = new JobTemplate
        {
            Fields =
            [
                new TemplateField { Name = "reportName", Type = FieldType.String },
                new TemplateField { Name = "first", Type = FieldType.Email, Required = false },
                new TemplateField { Name = "second", Type = FieldType.Email, Required = false }
            ]
        }
    };

    private static StepContext Context(Job job, string key = "run-key", Dictionary<PipelineStep, string>? prior = null) =>
        new(job, new JobRun { IdempotencyKey = key }, prior ?? new Dictionary<PipelineStep, string>());

    [Fact]
    public async Task SendEmail_UsesFirstFilledEmailField_AndRunScopedIdempotencyKey()
    {
        var sender = new Mock<IEmailSender>();
        var calc = JsonSerializer.Serialize(new CalculationResult("all good", new Dictionary<string, decimal>()));
        var ctx = Context(JobWith("""{"second":"b@x.com"}"""), prior: new() { [PipelineStep.Calculate] = calc });

        var output = await new SendEmailStep(sender.Object).ExecuteAsync(ctx, default);

        sender.Verify(s => s.SendAsync(
            It.Is<EmailMessage>(m => m.To == "b@x.com" && m.IdempotencyKey == "run-key:email" && m.Body.Contains("all good")),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("b@x.com", output);
    }

    [Fact]
    public async Task SendEmail_ApprovalJob_IsTheApprovalRequest_WithTheSameIdempotencyKey()
    {
        var sender = new Mock<IEmailSender>();
        var job = JobWith("""{"first":"approver@x.com"}""");
        job.Template!.RequiresApproval = true;

        await new SendEmailStep(sender.Object).ExecuteAsync(Context(job), default);

        sender.Verify(s => s.SendAsync(
            It.Is<EmailMessage>(m => m.To == "approver@x.com" && m.Subject.Contains("Approval needed")
                                     && m.IdempotencyKey == "run-key:email"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendEmail_NoRecipient_SkipsWithoutSending()
    {
        var sender = new Mock<IEmailSender>();

        var output = await new SendEmailStep(sender.Object).ExecuteAsync(Context(JobWith("{}")), default);

        sender.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("skipped", output);
    }

    [Fact]
    public async Task Calculate_WithoutDownloadOutput_Throws()
    {
        var engine = new Mock<ICalculationEngine>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CalculateStep(engine.Object).ExecuteAsync(Context(JobWith("{}")), default));
    }

    [Fact]
    public async Task Calculate_PassesDownloadedReportAndConfigToEngine()
    {
        var report = new ReportData("r1", [new ReportRow("A", 1m)]);
        var engine = new Mock<ICalculationEngine>();
        engine.Setup(e => e.CalculateAsync(It.IsAny<Guid>(), It.IsAny<ReportData>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CalculationResult("done", new Dictionary<string, decimal>()));
        var ctx = Context(JobWith("""{"reportName":"P"}"""),
            prior: new() { [PipelineStep.DownloadReport] = JsonSerializer.Serialize(report) });

        await new CalculateStep(engine.Object).ExecuteAsync(ctx, default);

        engine.Verify(e => e.CalculateAsync(
            ctx.Job.Id,
            It.Is<ReportData>(r => r.ReportId == "r1" && r.Rows.Count == 1),
            It.Is<IReadOnlyDictionary<string, string>>(c => c["reportName"] == "P"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Download_UsesConfiguredReportName_AndRunScopedKey()
    {
        var finance = new Mock<IFinanceAppClient>();
        finance.Setup(f => f.TriggerReportAsync("run-key:download", "Positions", It.IsAny<CancellationToken>())).ReturnsAsync("r9");
        finance.Setup(f => f.DownloadReportAsync("r9", It.IsAny<CancellationToken>())).ReturnsAsync(new ReportData("r9", []));

        var output = await new DownloadReportStep(finance.Object)
            .ExecuteAsync(Context(JobWith("""{"reportName":"Positions"}""")), default);

        Assert.Contains("r9", output);
    }

    [Fact]
    public async Task Download_FallsBackToJobName_WhenNoReportNameConfigured()
    {
        var finance = new Mock<IFinanceAppClient>();
        finance.Setup(f => f.TriggerReportAsync(It.IsAny<string>(), "Recon", It.IsAny<CancellationToken>())).ReturnsAsync("r1");
        finance.Setup(f => f.DownloadReportAsync("r1", It.IsAny<CancellationToken>())).ReturnsAsync(new ReportData("r1", []));

        await new DownloadReportStep(finance.Object).ExecuteAsync(Context(JobWith("{}")), default);

        finance.Verify(f => f.TriggerReportAsync(It.IsAny<string>(), "Recon", It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class JobStateTests
{
    [Theory]
    [InlineData(JobStatus.Scheduled)]
    [InlineData(JobStatus.Failed)]
    public void MarkInProgress_AllowedFromScheduledAndFailed(JobStatus from)
    {
        var job = new Job { Status = from };
        job.MarkInProgress();
        Assert.Equal(JobStatus.InProgress, job.Status);
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.NeedsManualAction)]
    public void MarkInProgress_RejectedFromOtherStates(JobStatus from) =>
        Assert.Throws<InvalidJobStateException>(() => new Job { Status = from }.MarkInProgress());

    [Fact]
    public void CompleteAndFail_OnlyFromInProgress()
    {
        Assert.Throws<InvalidJobStateException>(() => new Job { Status = JobStatus.Scheduled }.MarkCompleted());
        Assert.Throws<InvalidJobStateException>(() => new Job { Status = JobStatus.Scheduled }.MarkFailed());

        var done = new Job { Status = JobStatus.InProgress };
        done.MarkCompleted();
        Assert.Equal(JobStatus.Completed, done.Status);

        var failed = new Job { Status = JobStatus.InProgress };
        failed.MarkFailed();
        Assert.Equal(JobStatus.Failed, failed.Status);
    }
}
