using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using Moq;

namespace JobScheduler.Tests.Runs;

public class RunOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Guid _user = Guid.NewGuid();

    private readonly Job _job = new()
    {
        Name = "j",
        ScheduleType = ScheduleType.Manual,
        Status = JobStatus.Scheduled
    };

    public RunOrchestratorTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
    }

    private RunOrchestrator Sut() => new(_jobs.Object, _runs.Object, _queue.Object, _clock.Object);

    private void CaptureAdded(out Func<JobRun?> get)
    {
        JobRun? added = null;
        _runs.Setup(r => r.AddAsync(It.IsAny<JobRun>(), It.IsAny<CancellationToken>()))
            .Callback<JobRun, CancellationToken>((r, _) => added = r).Returns(Task.CompletedTask);
        get = () => added;
    }

    // ---- manual ----

    [Fact]
    public async Task Manual_CreatesPendingRun_MovesJobInProgress_AndEnqueuesOnce()
    {
        CaptureAdded(out var added);

        var run = await Sut().EnqueueManualAsync(_job.Id, "abc", _user, default);

        Assert.Same(added(), run);
        Assert.Equal($"{_job.Id}:manual:abc", run.IdempotencyKey);
        Assert.Equal(RunStatus.Pending, run.Status);
        Assert.Equal(_user, run.TriggeredByUserId);
        Assert.Equal(JobStatus.InProgress, _job.Status);
        _runs.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _queue.Verify(q => q.EnqueueAsync(run.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Manual_SameIdempotencyKey_ReturnsExistingRun_WithoutCreatingOrEnqueuing()
    {
        var existing = new JobRun { JobId = _job.Id, IdempotencyKey = $"{_job.Id}:manual:abc" };
        _runs.Setup(r => r.FindByKeyAsync(existing.IdempotencyKey, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var run = await Sut().EnqueueManualAsync(_job.Id, "abc", _user, default);

        Assert.Same(existing, run);
        _runs.Verify(r => r.AddAsync(It.IsAny<JobRun>(), It.IsAny<CancellationToken>()), Times.Never);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(JobStatus.Scheduled, _job.Status); // untouched
    }

    [Fact]
    public async Task Manual_SameKeyOnAlreadyCompletedJob_StillReturnsExistingRun()
    {
        _job.Status = JobStatus.Completed;
        var existing = new JobRun { JobId = _job.Id, IdempotencyKey = $"{_job.Id}:manual:abc", Status = RunStatus.Succeeded };
        _runs.Setup(r => r.FindByKeyAsync(existing.IdempotencyKey, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var run = await Sut().EnqueueManualAsync(_job.Id, "abc", _user, default);

        Assert.Same(existing, run); // a client retry of the same request is safe, not a 409
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Failed)]
    public async Task Manual_JobNotScheduled_Conflict(JobStatus status)
    {
        _job.Status = status;

        await Assert.ThrowsAsync<ConflictException>(() => Sut().EnqueueManualAsync(_job.Id, null, _user, default));

        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Manual_UnknownJob_NotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().EnqueueManualAsync(Guid.NewGuid(), null, _user, default));

    [Fact]
    public async Task Manual_WithoutClientKey_GeneratesDistinctKeys()
    {
        var first = await Sut().EnqueueManualAsync(_job.Id, null, _user, default);
        _job.Status = JobStatus.Scheduled;
        var second = await Sut().EnqueueManualAsync(_job.Id, "  ", _user, default);

        Assert.NotEqual(first.IdempotencyKey, second.IdempotencyKey);
    }

    [Fact]
    public async Task Manual_VeryLongClientKey_IsTruncated()
    {
        var run = await Sut().EnqueueManualAsync(_job.Id, new string('x', 500), _user, default);

        Assert.Equal($"{_job.Id}:manual:".Length + 100, run.IdempotencyKey.Length);
    }

    // ---- scheduled ----

    private void MakeFixed()
    {
        _job.ScheduleType = ScheduleType.Fixed;
        _job.RunAtUtc = new DateTime(2026, 10, 6, 4, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public async Task Scheduled_UsesKeyDerivedFromRunTime_AndHasNoUser()
    {
        MakeFixed();

        var run = await Sut().EnqueueScheduledAsync(_job.Id, null, default);

        Assert.Equal($"{_job.Id}:fixed:{_job.RunAtUtc:O}", run.IdempotencyKey);
        Assert.Null(run.TriggeredByUserId);
        Assert.Equal(JobStatus.InProgress, _job.Status);
        _queue.Verify(q => q.EnqueueAsync(run.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Scheduled_DuplicateFire_MapsToSameRun()
    {
        MakeFixed();
        var existing = new JobRun { JobId = _job.Id, IdempotencyKey = $"{_job.Id}:fixed:{_job.RunAtUtc:O}" };
        _runs.Setup(r => r.FindByKeyAsync(existing.IdempotencyKey, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var run = await Sut().EnqueueScheduledAsync(_job.Id, null, default);

        Assert.Same(existing, run);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Scheduled_CancelledBeforeFiring_Conflict()
    {
        MakeFixed();
        _job.Status = JobStatus.Cancelled;

        await Assert.ThrowsAsync<ConflictException>(() => Sut().EnqueueScheduledAsync(_job.Id, null, default));
    }

    [Fact]
    public async Task Scheduled_NonFixedJob_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().EnqueueScheduledAsync(_job.Id, null, default));

    // ---- retry ----

    [Fact]
    public async Task Retry_FailedJob_ResetsRunAndRequeuesSameRun()
    {
        _job.Status = JobStatus.Failed;
        var failed = new JobRun
        {
            JobId = _job.Id, Status = RunStatus.Failed, AutoRetriesUsed = 3, Error = "boom",
            FailedStep = PipelineStep.Calculate, FinishedAtUtc = DateTime.UtcNow
        };
        _runs.Setup(r => r.FindLatestForJobAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(failed);

        var run = await Sut().RetryAsync(_job.Id, _user, default);

        Assert.Same(failed, run);
        Assert.Equal(RunStatus.Pending, run.Status);
        Assert.Equal(0, run.AutoRetriesUsed);
        Assert.Null(run.Error);
        Assert.Null(run.FailedStep);
        Assert.Null(run.FinishedAtUtc);
        Assert.Equal(_user, run.TriggeredByUserId);
        Assert.Equal(JobStatus.InProgress, _job.Status);
        _runs.Verify(r => r.AddAsync(It.IsAny<JobRun>(), It.IsAny<CancellationToken>()), Times.Never); // same run, not a new one
        _queue.Verify(q => q.EnqueueAsync(failed.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(JobStatus.Scheduled)]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    public async Task Retry_NonFailedJob_Conflict(JobStatus status)
    {
        _job.Status = status;

        await Assert.ThrowsAsync<ConflictException>(() => Sut().RetryAsync(_job.Id, _user, default));
    }

    [Fact]
    public async Task Retry_FailedJobWithNoRun_Conflict()
    {
        _job.Status = JobStatus.Failed;

        await Assert.ThrowsAsync<ConflictException>(() => Sut().RetryAsync(_job.Id, _user, default));
    }
}
