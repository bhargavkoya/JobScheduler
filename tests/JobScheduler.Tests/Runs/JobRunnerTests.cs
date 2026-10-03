using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JobScheduler.Tests.Runs;

/// <summary>Counts executions so tests can prove a step ran exactly once across retries.</summary>
public sealed class FakeStep(PipelineStep name, Func<StepContext, int, string>? behavior = null) : IPipelineStep
{
    public PipelineStep Name { get; } = name;
    public int Calls { get; private set; }
    public StepContext? LastContext { get; private set; }

    public Task<string> ExecuteAsync(StepContext context, CancellationToken ct)
    {
        Calls++;
        LastContext = context;
        return Task.FromResult(behavior?.Invoke(context, Calls) ?? $"{Name} output");
    }
}

public class JobRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<TimeProvider> _clock = new();

    private readonly Job _job = new()
    {
        Status = JobStatus.InProgress,
        Name = "j",
        RetryPolicy = new RetryPolicy { MaxAutoRetries = 2, BackoffSeconds = 10 }
    };

    private readonly JobRun _run;

    private readonly FakeStep _download = new(PipelineStep.DownloadReport);
    private FakeStep _calculate = new(PipelineStep.Calculate);
    private readonly FakeStep _email = new(PipelineStep.SendEmail);

    public JobRunnerTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _run = new JobRun { JobId = _job.Id, IdempotencyKey = "k", Status = RunStatus.Pending };
        _runs.Setup(r => r.FindAsync(_run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_run);
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
    }

    // Steps passed out of order on purpose: the runner must order them by pipeline position.
    private JobRunner Sut() => new(
        _runs.Object, _jobs.Object, [_email, _calculate, _download], _queue.Object, _clock.Object,
        NullLogger<JobRunner>.Instance);

    private static FakeStep Failing(PipelineStep step, int failFirstCalls) => new(step, (_, call) =>
        call <= failFirstCalls ? throw new InvalidOperationException($"{step} boom {call}") : $"{step} ok");

    [Fact]
    public async Task AllStepsSucceed_CompletesRunAndJob_InPipelineOrder()
    {
        var order = new List<PipelineStep>();
        var download = new FakeStep(PipelineStep.DownloadReport, (_, _) => { order.Add(PipelineStep.DownloadReport); return "d"; });
        _calculate = new FakeStep(PipelineStep.Calculate, (_, _) => { order.Add(PipelineStep.Calculate); return "c"; });
        var email = new FakeStep(PipelineStep.SendEmail, (_, _) => { order.Add(PipelineStep.SendEmail); return "e"; });
        var sut = new JobRunner(_runs.Object, _jobs.Object, [email, _calculate, download], _queue.Object, _clock.Object,
            NullLogger<JobRunner>.Instance);

        await sut.ExecuteAsync(_run.Id, default);

        Assert.Equal([PipelineStep.DownloadReport, PipelineStep.Calculate, PipelineStep.SendEmail], order);
        Assert.Equal(RunStatus.Succeeded, _run.Status);
        Assert.Equal(JobStatus.Completed, _job.Status);
        Assert.Equal(1, _run.Attempt);
        Assert.Equal(3, _run.Steps.Count(s => s.Status == StepStatus.Succeeded));
        Assert.NotNull(_run.FinishedAtUtc);
        _runs.Verify(r => r.AddStep(It.IsAny<JobRunStep>()), Times.Exactly(3));
    }

    [Fact]
    public async Task LaterSteps_ReceiveEarlierStepOutput()
    {
        var download = new FakeStep(PipelineStep.DownloadReport, (_, _) => "report-json");
        _calculate = new FakeStep(PipelineStep.Calculate);
        var sut = new JobRunner(_runs.Object, _jobs.Object, [download, _calculate, _email], _queue.Object, _clock.Object,
            NullLogger<JobRunner>.Instance);

        await sut.ExecuteAsync(_run.Id, default);

        Assert.Equal("report-json", _calculate.LastContext!.PriorOutputs[PipelineStep.DownloadReport]);
    }

    [Fact]
    public async Task StepFails_WithRetriesLeft_GoesPending_AndRequeuesWithBackoff()
    {
        _calculate = Failing(PipelineStep.Calculate, failFirstCalls: 1);

        await Sut().ExecuteAsync(_run.Id, default);

        Assert.Equal(RunStatus.Pending, _run.Status);
        Assert.Equal(1, _run.AutoRetriesUsed);
        Assert.Equal(PipelineStep.Calculate, _run.FailedStep);
        Assert.Contains("boom", _run.Error);
        Assert.Equal(JobStatus.InProgress, _job.Status); // still in progress while retries remain
        Assert.Equal(0, _email.Calls);                    // later steps don't run after a failure
        Assert.Contains(_run.Steps, s => s.Step == PipelineStep.Calculate && s.Status == StepStatus.Failed);
        _queue.Verify(q => q.EnqueueAsync(_run.Id, TimeSpan.FromSeconds(10), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecondAutoRetry_UsesDoubleBackoff()
    {
        _run.AutoRetriesUsed = 1;
        _calculate = Failing(PipelineStep.Calculate, failFirstCalls: 1);

        await Sut().ExecuteAsync(_run.Id, default);

        Assert.Equal(2, _run.AutoRetriesUsed);
        _queue.Verify(q => q.EnqueueAsync(_run.Id, TimeSpan.FromSeconds(20), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetriesExhausted_FailsRunAndJob_AndDoesNotRequeue()
    {
        _run.AutoRetriesUsed = 2; // == MaxAutoRetries
        _calculate = Failing(PipelineStep.Calculate, failFirstCalls: 1);

        await Sut().ExecuteAsync(_run.Id, default);

        Assert.Equal(RunStatus.Failed, _run.Status);
        Assert.Equal(JobStatus.Failed, _job.Status);
        Assert.Equal(PipelineStep.Calculate, _run.FailedStep);
        Assert.NotNull(_run.FinishedAtUtc);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ZeroRetryPolicy_FailsImmediately()
    {
        _job.RetryPolicy = new RetryPolicy { MaxAutoRetries = 0, BackoffSeconds = 0 };
        _calculate = Failing(PipelineStep.Calculate, failFirstCalls: 1);

        await Sut().ExecuteAsync(_run.Id, default);

        Assert.Equal(RunStatus.Failed, _run.Status);
        Assert.Equal(JobStatus.Failed, _job.Status);
    }

    [Fact]
    public async Task Retry_ResumesAfterLastGoodStep_NeverRepeatingSucceededSteps()
    {
        _calculate = Failing(PipelineStep.Calculate, failFirstCalls: 1);
        var sut = Sut();

        await sut.ExecuteAsync(_run.Id, default); // attempt 1: download ok, calculate fails
        await sut.ExecuteAsync(_run.Id, default); // attempt 2 (the queued retry): resumes at calculate

        Assert.Equal(RunStatus.Succeeded, _run.Status);
        Assert.Equal(2, _run.Attempt);
        Assert.Equal(1, _download.Calls); // not downloaded twice
        Assert.Equal(2, _calculate.Calls);
        Assert.Equal(1, _email.Calls);    // email sent exactly once
        Assert.Equal(JobStatus.Completed, _job.Status);
    }

    [Fact]
    public async Task Retry_AfterManualReset_ReusesStoredOutputOfEarlierSteps()
    {
        _run.Steps.Add(new JobRunStep
        {
            RunId = _run.Id, Step = PipelineStep.DownloadReport, Status = StepStatus.Succeeded, Output = "stored-report"
        });
        _run.Attempt = 1;

        await Sut().ExecuteAsync(_run.Id, default);

        Assert.Equal(0, _download.Calls);
        Assert.Equal("stored-report", _calculate.LastContext!.PriorOutputs[PipelineStep.DownloadReport]);
        Assert.Equal(2, _run.Attempt);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    public async Task RedeliveryOfFinishedRun_IsNoOp(RunStatus status)
    {
        _run.Status = status;

        await Sut().ExecuteAsync(_run.Id, default);

        Assert.Equal(0, _download.Calls + _calculate.Calls + _email.Calls);
        Assert.Equal(0, _run.Attempt);
    }

    [Fact]
    public async Task UnknownRun_IsIgnored()
    {
        await Sut().ExecuteAsync(Guid.NewGuid(), default);

        Assert.Equal(0, _download.Calls);
    }

    [Fact]
    public async Task Shutdown_MidRun_RethrowsAndLeavesRunRunning()
    {
        using var cts = new CancellationTokenSource();
        _calculate = new FakeStep(PipelineStep.Calculate, (_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sut().ExecuteAsync(_run.Id, cts.Token));

        Assert.Equal(RunStatus.Running, _run.Status); // StartupRecovery re-queues it on next boot
        Assert.Equal(JobStatus.InProgress, _job.Status);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(10, 1, 10)]
    [InlineData(10, 2, 20)]
    [InlineData(10, 3, 40)]
    [InlineData(2000, 3, 3600)] // capped
    public void Backoff_IsExponential_AndCapped(int backoff, int retry, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), JobRunner.Backoff(backoff, retry));
}
