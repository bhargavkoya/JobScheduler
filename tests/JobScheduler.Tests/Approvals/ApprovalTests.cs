using System.Text.Json;
using JobScheduler.Application.Approvals;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JobScheduler.Tests.Approvals;

public class JobApprovalStateTests
{
    [Fact]
    public void RequestApproval_OnlyFromInProgress_ThenApproveCompletes()
    {
        var job = new Job { Status = JobStatus.InProgress };
        job.RequestApproval();
        Assert.Equal(JobStatus.NeedsManualAction, job.Status);

        job.Approve();
        Assert.Equal(JobStatus.Completed, job.Status);
    }

    [Fact]
    public void Reject_FailsTheJob_SoTheNormalRetryPathApplies()
    {
        var job = new Job { Status = JobStatus.NeedsManualAction };
        job.Reject();
        Assert.Equal(JobStatus.Failed, job.Status);
        job.MarkInProgress(); // retry
    }

    [Theory]
    [InlineData(JobStatus.Scheduled)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void RequestApproval_FromOtherStates_Throws(JobStatus status)
    {
        Assert.Throws<InvalidJobStateException>(() => new Job { Status = status }.RequestApproval());
    }

    [Theory]
    [InlineData(JobStatus.Scheduled)]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    public void ApproveAndReject_WhenNotWaiting_Throw(JobStatus status)
    {
        Assert.Throws<InvalidJobStateException>(() => new Job { Status = status }.Approve());
        Assert.Throws<InvalidJobStateException>(() => new Job { Status = status }.Reject());
    }

    [Fact]
    public void WaitingJob_CanStillBeCancelled()
    {
        var job = new Job { Status = JobStatus.NeedsManualAction };
        job.Cancel();
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }

    [Fact]
    public void StatusChange_StampsStatusChangedAt()
    {
        var job = new Job { Status = JobStatus.InProgress, StatusChangedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        job.RequestApproval();
        Assert.True(job.StatusChangedAtUtc > new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}

public class ApprovalServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IApprovalStore> _approvals = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Mock<TimeProvider> _clock = new();

    private readonly Guid _approverId = Guid.NewGuid();
    private readonly Job _job;
    private readonly JobRun _run = new() { IdempotencyKey = "k", Attempt = 1 };
    private readonly List<JobApproval> _saved = [];

    public ApprovalServiceTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _job = new Job
        {
            Name = "Alloc",
            Team = Team.Business,
            Status = JobStatus.NeedsManualAction,
            ApproverUserId = _approverId,
            StatusChangedAtUtc = Now.UtcDateTime
        };
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
        _runs.Setup(r => r.FindLatestForJobAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_run);
        _approvals.Setup(a => a.AddAsync(It.IsAny<JobApproval>(), It.IsAny<CancellationToken>()))
            .Callback<JobApproval, CancellationToken>((a, _) => _saved.Add(a)).Returns(Task.CompletedTask);

        SetUser(_approverId, Role.Employee, Team.Business, Permissions.ApproveJobs);
    }

    private void SetUser(Guid id, Role role, Team team, params string[] permissions)
    {
        _me.SetupGet(m => m.UserId).Returns(id);
        _me.SetupGet(m => m.Role).Returns(role);
        _me.SetupGet(m => m.Team).Returns(team);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(permissions);
    }

    private ApprovalService Sut() => new(
        _jobs.Object, _runs.Object, _approvals.Object, _users.Object, _scheduler.Object, _me.Object,
        _clock.Object, AtRiskPolicy.Default);

    [Fact]
    public async Task NamedApprover_Approves_JobCompletes_DecisionRecorded_FollowUpCancelled()
    {
        var dto = await Sut().ApproveAsync(_job.Id, " looks good ", default);

        Assert.Equal(JobStatus.Completed, _job.Status);
        Assert.Equal(JobStatus.Completed, dto.Status);
        var approval = Assert.Single(_saved);
        Assert.Equal(ApprovalDecision.Approved, approval.Decision);
        Assert.Equal("looks good", approval.Comment);
        Assert.Equal(_approverId, approval.ApproverUserId);
        Assert.Equal(_run.Id, approval.RunId);
        _jobs.Verify(j => j.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _scheduler.Verify(s => s.UnscheduleAsync(_job.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reject_FailsTheJob_AndRequiresAComment()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Sut().RejectAsync(_job.Id, "  ", default));
        Assert.Equal(JobStatus.NeedsManualAction, _job.Status);

        await Sut().RejectAsync(_job.Id, "amount too high", default);

        Assert.Equal(JobStatus.Failed, _job.Status);
        Assert.Equal(ApprovalDecision.Rejected, Assert.Single(_saved).Decision);
    }

    [Fact]
    public async Task DecidingTwice_SecondCallIsAConflict_AndRecordsNothingMore()
    {
        await Sut().ApproveAsync(_job.Id, null, default);

        await Assert.ThrowsAsync<ConflictException>(() => Sut().ApproveAsync(_job.Id, null, default));
        await Assert.ThrowsAsync<ConflictException>(() => Sut().RejectAsync(_job.Id, "no", default));
        Assert.Single(_saved);
    }

    [Fact]
    public async Task SomeoneElse_IsForbidden()
    {
        SetUser(Guid.NewGuid(), Role.Employee, Team.Business, Permissions.ApproveJobs);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().ApproveAsync(_job.Id, null, default));
        Assert.Equal(JobStatus.NeedsManualAction, _job.Status);
    }

    [Fact]
    public async Task NamedApproverWithoutTheClaim_IsForbidden()
    {
        SetUser(_approverId, Role.Employee, Team.Business);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().ApproveAsync(_job.Id, null, default));
    }

    [Fact]
    public async Task Admin_CanOverride()
    {
        SetUser(Guid.NewGuid(), Role.Admin, Team.Technical);

        await Sut().ApproveAsync(_job.Id, null, default);

        Assert.Equal(JobStatus.Completed, _job.Status);
    }

    [Fact]
    public async Task JobFromAnInvisibleTeam_IsNotFound()
    {
        _job.Team = Team.Technical;

        await Assert.ThrowsAsync<NotFoundException>(() => Sut().ApproveAsync(_job.Id, null, default));
    }

    [Fact]
    public async Task Queue_ListsOnlyVisibleWaitingJobs_OldestFirst_WithCanDecide()
    {
        var older = new Job { Name = "older", Team = Team.Business, Status = JobStatus.NeedsManualAction,
            ApproverUserId = Guid.NewGuid(), StatusChangedAtUtc = Now.UtcDateTime.AddHours(-2) };
        JobQuery? seen = null;
        _jobs.Setup(j => j.ListAsync(It.IsAny<JobQuery>(), It.IsAny<CancellationToken>()))
            .Callback<JobQuery, CancellationToken>((q, _) => seen = q)
            .ReturnsAsync([_job, older]);
        _users.Setup(u => u.FindByIdAsync(_approverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Email = "appr@x.com" });

        var queue = await Sut().ListQueueAsync(default);

        Assert.Equal(JobStatus.NeedsManualAction, seen!.Status);
        Assert.Equal([Team.Business], seen.VisibleTeams);
        Assert.Equal(["older", "Alloc"], queue.Select(q => q.Job.Name));
        Assert.True(queue[0].Job.IsAtRisk);   // waiting 2h against a 15 min limit
        Assert.False(queue[1].Job.IsAtRisk);
        Assert.False(queue[0].CanDecide);   // someone else's job
        Assert.True(queue[1].CanDecide);    // mine
        Assert.Equal("appr@x.com", queue[1].ApproverEmail);
    }

    [Fact]
    public async Task History_ReturnsDecisionsInOrder_WithApproverEmail()
    {
        _approvals.Setup(a => a.ListForJobAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new JobApproval { JobId = _job.Id, ApproverUserId = _approverId, Decision = ApprovalDecision.Approved, DecidedAtUtc = Now.UtcDateTime },
            new JobApproval { JobId = _job.Id, ApproverUserId = _approverId, Decision = ApprovalDecision.Rejected, DecidedAtUtc = Now.UtcDateTime.AddHours(-1) }
        ]);
        _users.Setup(u => u.FindByIdAsync(_approverId, It.IsAny<CancellationToken>())).ReturnsAsync(new User { Email = "appr@x.com" });

        var history = await Sut().ListForJobAsync(_job.Id, default);

        Assert.Equal([ApprovalDecision.Rejected, ApprovalDecision.Approved], history.Select(h => h.Decision));
        Assert.All(history, h => Assert.Equal("appr@x.com", h.ApproverEmail));
    }
}

public class AtRiskTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(15);

    private static string? Eval(Job job) => AtRiskEvaluator.Evaluate(job, Now, Limit);

    [Fact]
    public void ScheduledFixed_PastItsTime_IsAtRisk()
    {
        var job = new Job { Status = JobStatus.Scheduled, ScheduleType = ScheduleType.Fixed, RunAtUtc = Now.AddMinutes(-5) };
        Assert.Contains("has not started", Eval(job));
    }

    [Fact]
    public void ScheduledFixed_JustDueOrFuture_IsNotAtRisk()
    {
        Assert.Null(Eval(new Job { Status = JobStatus.Scheduled, ScheduleType = ScheduleType.Fixed, RunAtUtc = Now.AddSeconds(-30) }));
        Assert.Null(Eval(new Job { Status = JobStatus.Scheduled, ScheduleType = ScheduleType.Fixed, RunAtUtc = Now.AddHours(1) }));
    }

    [Fact]
    public void ScheduledManual_NeverAtRisk()
    {
        Assert.Null(Eval(new Job { Status = JobStatus.Scheduled, ScheduleType = ScheduleType.Manual }));
    }

    [Theory]
    [InlineData(JobStatus.InProgress, "Running")]
    [InlineData(JobStatus.NeedsManualAction, "Waiting")]
    public void StuckTooLong_IsAtRisk(JobStatus status, string expected)
    {
        var job = new Job { Status = status, StatusChangedAtUtc = Now.AddMinutes(-16) };
        Assert.StartsWith(expected, Eval(job));
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.NeedsManualAction)]
    public void WithinThreshold_IsNotAtRisk(JobStatus status)
    {
        Assert.Null(Eval(new Job { Status = status, StatusChangedAtUtc = Now.AddMinutes(-14) }));
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void FinishedJobs_AreNeverAtRisk(JobStatus status)
    {
        Assert.Null(Eval(new Job { Status = status, StatusChangedAtUtc = Now.AddDays(-3) }));
    }
}

public class ApprovalFollowUpTests
{
    private static readonly DateTime Since = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly Mock<IJobScheduler> _scheduler = new();

    private readonly User _approver = new() { Email = "appr@x.com" };
    private readonly Job _job;
    private readonly JobRun _run = new() { IdempotencyKey = "run-key", Attempt = 1 };

    public ApprovalFollowUpTests()
    {
        _job = new Job
        {
            Name = "Alloc",
            Status = JobStatus.NeedsManualAction,
            ApproverUserId = _approver.Id,
            StatusChangedAtUtc = Since,
            ConfigJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["followUpAfterMinutes"] = "30" })
        };
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
        _users.Setup(u => u.FindByIdAsync(_approver.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_approver);
        _runs.Setup(r => r.FindLatestForJobAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_run);
    }

    private ApprovalFollowUp Sut() =>
        new(_jobs.Object, _runs.Object, _users.Object, _email.Object, _scheduler.Object, NullLogger<ApprovalFollowUp>.Instance);

    [Fact]
    public async Task Schedule_AnchorsOnWhenTheJobStartedWaiting()
    {
        await Sut().ScheduleAsync(_job, default);

        _scheduler.Verify(s => s.ScheduleFollowUpAsync(_job.Id, Since.AddMinutes(30), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    public async Task Schedule_WithoutAUsableDelay_SchedulesNothing(string? value)
    {
        var config = new Dictionary<string, string>();
        if (value is not null) config["followUpAfterMinutes"] = value;
        _job.ConfigJson = JsonSerializer.Serialize(config);

        await Sut().ScheduleAsync(_job, default);

        _scheduler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Fire_WhileStillWaiting_EmailsTheApproverOnce_WithAStableKey()
    {
        var keys = new List<string>();
        _email.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => keys.Add(m.IdempotencyKey)).Returns(Task.CompletedTask);

        await Sut().FireAsync(_job.Id, default);
        await Sut().FireAsync(_job.Id, default); // e.g. restart recovery fires it again

        _email.Verify(e => e.SendAsync(It.Is<EmailMessage>(m => m.To == "appr@x.com" && m.Subject.Contains("Alloc")),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Single(keys.Distinct()); // the idempotent sender collapses both into one real email
        Assert.Equal("run-key:followup:1", keys[0]);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public async Task Fire_AfterSomeoneActed_SendsNothing(JobStatus status)
    {
        _job.Status = status;

        await Sut().FireAsync(_job.Id, default);

        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Fire_UnknownJobOrMissingApprover_SendsNothing()
    {
        await Sut().FireAsync(Guid.NewGuid(), default);

        _users.Setup(u => u.FindByIdAsync(_approver.Id, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        await Sut().FireAsync(_job.Id, default);

        _email.VerifyNoOtherCalls();
    }
}
