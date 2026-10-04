using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JobScheduler.Tests.Runs;

public class RecurrenceBuilderTests
{
    [Theory]
    [InlineData(RecurrenceFrequency.Daily, "09:30", null, null, "0 30 9 * * ?", "Daily at 09:30 IST")]
    [InlineData(RecurrenceFrequency.Weekly, "18:00", 1, null, "0 0 18 ? * MON", "Weekly on Monday at 18:00 IST")]
    [InlineData(RecurrenceFrequency.Monthly, "06:05", null, 15, "0 5 6 15 * ?", "Monthly on day 15 at 06:05 IST")]
    public void Build_MapsToCron(RecurrenceFrequency f, string time, int? dow, int? dom, string cron, string text)
    {
        var s = RecurrenceBuilder.Build(new RecurrenceDto(f, time, dow, dom));
        Assert.Equal(cron, s.Cron);
        Assert.Equal(text, s.Text);
    }

    [Theory]
    [InlineData(RecurrenceFrequency.Daily, "25:00", null, null)]
    [InlineData(RecurrenceFrequency.Daily, "9am", null, null)]
    [InlineData(RecurrenceFrequency.Weekly, "09:00", null, null)]
    [InlineData(RecurrenceFrequency.Weekly, "09:00", 7, null)]
    [InlineData(RecurrenceFrequency.Monthly, "09:00", null, 31)]
    [InlineData(RecurrenceFrequency.Monthly, "09:00", null, 0)]
    public void Build_InvalidInput_Throws(RecurrenceFrequency f, string time, int? dow, int? dom) =>
        Assert.Throws<ValidationException>(() => RecurrenceBuilder.Build(new RecurrenceDto(f, time, dow, dom)));

    [Fact]
    public void Build_Null_Throws() => Assert.Throws<ValidationException>(() => RecurrenceBuilder.Build(null));
}

public class RecurrentAndChainedRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTime Tick = new(2026, 10, 5, 3, 30, 0, DateTimeKind.Utc);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Job _job = new() { Name = "r", ScheduleType = ScheduleType.Recurrent, RecurrenceCron = "0 0 9 * * ?", Status = JobStatus.Scheduled };

    public RecurrentAndChainedRunTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
    }

    private RunOrchestrator Sut() => new(_jobs.Object, _runs.Object, _queue.Object, _clock.Object);

    [Fact]
    public async Task Recurrent_KeyIsPerFiring()
    {
        var run = await Sut().EnqueueScheduledAsync(_job.Id, Tick, default);

        Assert.Equal($"{_job.Id}:recurrent:{Tick:O}", run.IdempotencyKey);
        Assert.Equal(JobStatus.InProgress, _job.Status);
        _queue.Verify(q => q.EnqueueAsync(run.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Recurrent_DoubleFireOfSameTick_ReturnsExistingRun()
    {
        var existing = new JobRun { JobId = _job.Id, IdempotencyKey = $"{_job.Id}:recurrent:{Tick:O}" };
        _runs.Setup(r => r.FindByKeyAsync(existing.IdempotencyKey, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var run = await Sut().EnqueueScheduledAsync(_job.Id, Tick, default);

        Assert.Same(existing, run);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recurrent_AfterPreviousRunCompleted_StartsAgain()
    {
        _job.Status = JobStatus.Completed;

        await Sut().EnqueueScheduledAsync(_job.Id, Tick, default);

        Assert.Equal(JobStatus.InProgress, _job.Status);
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.NeedsManualAction)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Recurrent_WhileBusyOrStopped_SkipsTheFiring(JobStatus status)
    {
        _job.Status = status;
        await Assert.ThrowsAsync<ConflictException>(() => Sut().EnqueueScheduledAsync(_job.Id, Tick, default));
    }

    [Fact]
    public void Recurrent_CompletedJobCanStillBeCancelled_ButFixedCannot()
    {
        _job.Status = JobStatus.Completed;
        Assert.True(_job.CanCancel);
        Assert.False(new Job { ScheduleType = ScheduleType.Fixed, Status = JobStatus.Completed }.CanCancel);
    }

    [Fact]
    public async Task Chained_KeyIsPerUpstreamRun()
    {
        _job.ScheduleType = ScheduleType.EventBased;
        var upstreamRun = Guid.NewGuid();

        var run = await Sut().EnqueueChainedAsync(_job.Id, upstreamRun, default);

        Assert.Equal($"{_job.Id}:chain:{upstreamRun}", run.IdempotencyKey);
        _queue.Verify(q => q.EnqueueAsync(run.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Chained_ReplayedCompletion_DoesNotEnqueueAgain()
    {
        _job.ScheduleType = ScheduleType.EventBased;
        var upstreamRun = Guid.NewGuid();
        var existing = new JobRun { JobId = _job.Id, IdempotencyKey = $"{_job.Id}:chain:{upstreamRun}" };
        _runs.Setup(r => r.FindByKeyAsync(existing.IdempotencyKey, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        Assert.Same(existing, await Sut().EnqueueChainedAsync(_job.Id, upstreamRun, default));
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Chained_NonEventBasedJob_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().EnqueueChainedAsync(_job.Id, Guid.NewGuid(), default));
}

public class JobChainerTests
{
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IRunOrchestrator> _orchestrator = new();

    [Fact]
    public async Task Completion_StartsEveryWaitingDependent_AndOneFailureDoesNotStopTheRest()
    {
        var upstream = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var bad = new Job { Name = "bad" };
        var good = new Job { Name = "good" };
        _jobs.Setup(j => j.ListWaitingDependentsAsync(upstream, It.IsAny<CancellationToken>())).ReturnsAsync([bad, good]);
        _orchestrator.Setup(o => o.EnqueueChainedAsync(bad.Id, runId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException("cancelled"));
        _orchestrator.Setup(o => o.EnqueueChainedAsync(good.Id, runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobRun());

        await new JobChainer(_jobs.Object, _orchestrator.Object, NullLogger<JobChainer>.Instance)
            .OnJobCompletedAsync(upstream, runId, default);

        _orchestrator.Verify(o => o.EnqueueChainedAsync(good.Id, runId, It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class JobServiceScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<ITemplateStore> _templates = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly Mock<IUserStore> _users = new();

    private readonly JobTemplate _template = new()
    {
        Name = "Recon",
        IsApproved = true,
        SupportedScheduleTypes = [ScheduleType.Manual, ScheduleType.Recurrent, ScheduleType.EventBased],
        Fields = [new TemplateField { Name = "report", Label = "Report", Type = FieldType.String, Required = true }],
        DefaultRetryPolicy = new RetryPolicy()
    };

    public JobServiceScheduleTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _me.SetupGet(m => m.UserId).Returns(Guid.NewGuid());
        _me.SetupGet(m => m.Role).Returns(Role.Employee);
        _me.SetupGet(m => m.Team).Returns(Team.Business);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());
        _templates.Setup(t => t.FindAsync(_template.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_template);
    }

    private JobService Sut() => new(_jobs.Object, _templates.Object, _me.Object, _clock.Object, _scheduler.Object, _users.Object, AtRiskPolicy.Default);

    private CreateJobRequest Request(ScheduleType type, RecurrenceDto? rec = null, Guid? trigger = null) =>
        new(_template.Id, "job", type, null, new Dictionary<string, string> { ["report"] = "r" }, null, rec, trigger);

    [Fact]
    public async Task Recurrent_StoresCron_AndRegistersTrigger()
    {
        Job? saved = null;
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((j, _) => saved = j).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(Request(ScheduleType.Recurrent, new RecurrenceDto(RecurrenceFrequency.Daily, "09:00", null, null)), default);

        Assert.Equal("0 0 9 * * ?", saved!.RecurrenceCron);
        Assert.Equal("Daily at 09:00 IST", dto.RecurrenceText);
        _scheduler.Verify(s => s.ScheduleRecurrentAsync(saved.Id, "0 0 9 * * ?", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Recurrent_WithoutRecurrence_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(ScheduleType.Recurrent), default));

    [Fact]
    public async Task Manual_WithRecurrence_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().CreateAsync(Request(ScheduleType.Manual, new RecurrenceDto(RecurrenceFrequency.Daily, "09:00", null, null)), default));

    [Fact]
    public async Task EventBased_LinksToUpstream_AndRegistersNoTrigger()
    {
        var upstream = new Job { Name = "up", Team = Team.Business, Status = JobStatus.Scheduled };
        _jobs.Setup(j => j.FindAsync(upstream.Id, It.IsAny<CancellationToken>())).ReturnsAsync(upstream);
        Job? saved = null;
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((j, _) => saved = j).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(Request(ScheduleType.EventBased, trigger: upstream.Id), default);

        Assert.Equal(upstream.Id, saved!.TriggerJobId);
        Assert.Equal(upstream.Id, dto.TriggerJobId);
        _scheduler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EventBased_WithoutTrigger_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(ScheduleType.EventBased), default));

    [Fact]
    public async Task EventBased_UnknownUpstream_NotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().CreateAsync(Request(ScheduleType.EventBased, trigger: Guid.NewGuid()), default));

    [Fact]
    public async Task EventBased_UpstreamAlreadyCancelled_Throws()
    {
        var upstream = new Job { Name = "up", Team = Team.Business, Status = JobStatus.Cancelled };
        _jobs.Setup(j => j.FindAsync(upstream.Id, It.IsAny<CancellationToken>())).ReturnsAsync(upstream);
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(ScheduleType.EventBased, trigger: upstream.Id), default));
    }
}

public class RunHistoryCsvTests
{
    private static JobRunDto Run(string? error = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), "key:1", RunStatus.Failed, 2, 1, error, PipelineStep.Calculate,
        new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 5, 0, 0, 5, DateTimeKind.Utc), null,
        [new RunStepDto(PipelineStep.DownloadReport, StepStatus.Succeeded, 1, "ok", DateTime.UtcNow, DateTime.UtcNow)]);

    [Fact]
    public void Build_HasHeader_AndRowInIst()
    {
        var lines = RunHistoryCsv.Build([Run()]).TrimEnd().Split("\r\n");

        Assert.StartsWith("RunId,IdempotencyKey,Status", lines[0]);
        Assert.Contains("Failed,2,1,Calculate,,2026-10-05 05:30:00,2026-10-05 05:30:05,,DownloadReport#1=Succeeded", lines[1]);
    }

    [Fact]
    public void Build_QuotesCommasAndQuotes()
    {
        var csv = RunHistoryCsv.Build([Run("bad, \"quoted\"")]);
        Assert.Contains("\"bad, \"\"quoted\"\"\"", csv);
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("plain", "plain")]
    public void Escape_NeutralisesFormulas(string input, string expected) => Assert.Equal(expected, RunHistoryCsv.Escape(input));
}
