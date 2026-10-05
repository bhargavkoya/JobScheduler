using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;
using Moq;

namespace JobScheduler.Tests.Jobs;

public class JobServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<ITemplateStore> _templates = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Guid _myId = Guid.NewGuid();

    private readonly JobTemplate _template = new()
    {
        Name = "Recon",
        IsApproved = true,
        SupportedScheduleTypes = [ScheduleType.Fixed, ScheduleType.Manual],
        Fields =
        [
            new TemplateField { Name = "report", Label = "Report", Type = FieldType.String, Required = true },
            new TemplateField { Name = "email", Label = "Email", Type = FieldType.Email, Required = true },
            new TemplateField { Name = "tolerance", Label = "Tolerance", Type = FieldType.Number, Required = false }
        ],
        DefaultRetryPolicy = new RetryPolicy { MaxAutoRetries = 2, BackoffSeconds = 15 }
    };

    public JobServiceTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _me.SetupGet(m => m.UserId).Returns(_myId);
        _me.SetupGet(m => m.Role).Returns(Role.Employee);
        _me.SetupGet(m => m.Team).Returns(Team.Business);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());
        _templates.Setup(t => t.FindAsync(_template.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_template);
    }

    private JobService Sut() => new(_jobs.Object, _templates.Object, _me.Object, _clock.Object, _scheduler.Object, _users.Object, AtRiskPolicy.Default);

    private static Dictionary<string, string> GoodConfig() => new() { ["report"] = "Positions", ["email"] = "a@b.com" };

    private CreateJobRequest Request(
        ScheduleType type = ScheduleType.Manual, DateTime? runAtIst = null,
        Dictionary<string, string>? config = null, RetryPolicyDto? retry = null) =>
        new(_template.Id, " My job ", type, runAtIst, config ?? GoodConfig(), retry);

    // ---- approver resolution ----

    private void RequireApproval()
    {
        _template.RequiresApproval = true;
        _template.Fields = [.. _template.Fields, new TemplateField { Name = "approverEmail", Label = "Approver", Type = FieldType.Email, Required = true }];
    }

    private Dictionary<string, string> ApprovalConfig(string email = "appr@x.com")
    {
        var config = GoodConfig();
        config["approverEmail"] = email;
        return config;
    }

    [Fact]
    public async Task Create_ApprovalTemplate_StoresTheResolvedApprover()
    {
        RequireApproval();
        var approver = new User { Email = "appr@x.com" };
        approver.SetPermissions([Permissions.ApproveJobs]);
        _users.Setup(u => u.FindByEmailAsync("appr@x.com", It.IsAny<CancellationToken>())).ReturnsAsync(approver);
        Job? saved = null;
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((j, _) => saved = j).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(Request(config: ApprovalConfig(" Appr@X.com ")), default);

        Assert.Equal(approver.Id, saved!.ApproverUserId);
        Assert.Equal(approver.Id, dto.ApproverUserId);
        Assert.True(dto.RequiresApproval);
    }

    [Fact]
    public async Task Create_ApprovalTemplate_UnknownApprover_IsRejected()
    {
        RequireApproval();

        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(config: ApprovalConfig()), default));
        _jobs.Verify(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ApprovalTemplate_ApproverWithoutPermission_IsRejected()
    {
        RequireApproval();
        _users.Setup(u => u.FindByEmailAsync("appr@x.com", It.IsAny<CancellationToken>())).ReturnsAsync(new User { Email = "appr@x.com" });

        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(config: ApprovalConfig()), default));
    }

    [Fact]
    public async Task Create_ApprovalTemplate_AdminApprover_IsAllowedWithoutTheClaim()
    {
        RequireApproval();
        _users.Setup(u => u.FindByEmailAsync("appr@x.com", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User { Email = "appr@x.com", Role = Role.Admin });

        var dto = await Sut().CreateAsync(Request(config: ApprovalConfig()), default);

        Assert.NotNull(dto.ApproverUserId);
    }

    [Fact]
    public async Task Create_PlainTemplate_HasNoApprover_AndNeverLooksOneUp()
    {
        var dto = await Sut().CreateAsync(Request(), default);

        Assert.Null(dto.ApproverUserId);
        Assert.False(dto.RequiresApproval);
        _users.VerifyNoOtherCalls();
    }

    // ---- create ----

    [Fact]
    public async Task Create_Manual_StoresOwnerTeamDefaultsAndScheduledStatus()
    {
        Job? saved = null;
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((j, _) => saved = j).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(Request(), default);

        Assert.NotNull(saved);
        Assert.Equal("My job", saved!.Name);
        Assert.Equal(_myId, saved.OwnerId);
        Assert.Equal(Team.Business, saved.Team);
        Assert.Equal(JobStatus.Scheduled, saved.Status);
        Assert.Null(saved.RunAtUtc);
        Assert.Equal(2, saved.RetryPolicy.MaxAutoRetries); // template default
        Assert.Equal(JobStatus.Scheduled, dto.Status);
        _jobs.Verify(j => j.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_Fixed_ConvertsIstToUtc()
    {
        Job? saved = null;
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((j, _) => saved = j).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(Request(ScheduleType.Fixed, new DateTime(2026, 10, 6, 9, 30, 0)), default);

        Assert.Equal(new DateTime(2026, 10, 6, 4, 0, 0), saved!.RunAtUtc); // 09:30 IST = 04:00 UTC
        Assert.Equal(new DateTime(2026, 10, 6, 9, 30, 0), dto.RunAtIst);
    }

    [Fact]
    public async Task Create_Fixed_RegistersTriggerWithScheduler()
    {
        await Sut().CreateAsync(Request(ScheduleType.Fixed, new DateTime(2026, 10, 6, 9, 30, 0)), default);

        _scheduler.Verify(s => s.ScheduleFixedAsync(
            It.IsAny<Guid>(), new DateTime(2026, 10, 6, 4, 0, 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_Manual_DoesNotTouchScheduler()
    {
        await Sut().CreateAsync(Request(), default);

        _scheduler.Verify(s => s.ScheduleFixedAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_Fixed_InThePast_Throws()
    {
        // 04:00 IST on the 5th is 22:30 UTC on the 4th, before "now" (00:00 UTC on the 5th).
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().CreateAsync(Request(ScheduleType.Fixed, new DateTime(2026, 10, 5, 4, 0, 0)), default));
    }

    [Fact]
    public async Task Create_Fixed_WithoutRunTime_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(ScheduleType.Fixed), default));

    [Fact]
    public async Task Create_Manual_WithRunTime_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().CreateAsync(Request(ScheduleType.Manual, new DateTime(2030, 1, 1)), default));

    [Fact]
    public async Task Create_UnapprovedTemplate_Throws()
    {
        _template.IsApproved = false;
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(), default));
        _jobs.Verify(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_UnknownTemplate_ThrowsNotFound()
    {
        var request = Request() with { TemplateId = Guid.NewGuid() };
        await Assert.ThrowsAsync<NotFoundException>(() => Sut().CreateAsync(request, default));
    }

    [Fact]
    public async Task Create_ScheduleTypeNotSupportedByTemplate_Throws()
    {
        _template.SupportedScheduleTypes = [ScheduleType.Manual];
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().CreateAsync(Request(ScheduleType.Fixed, new DateTime(2030, 1, 1)), default));
    }

    [Theory]
    [InlineData(ScheduleType.Recurrent)]
    [InlineData(ScheduleType.EventBased)]
    public async Task Create_RecurrentOrEventBased_WithoutTheirRequiredSettings_Throw(ScheduleType type)
    {
        _template.SupportedScheduleTypes = [type];
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(type), default));
    }

    [Fact]
    public async Task Create_TriggerBased_ReturnsTheTokenOnceAndStoresOnlyItsHash()
    {
        _template.SupportedScheduleTypes = [ScheduleType.TriggerBased];
        Job? saved = null;
        _jobs.Setup(j => j.AddAsync(It.IsAny<Job>(), It.IsAny<CancellationToken>()))
            .Callback<Job, CancellationToken>((j, _) => saved = j).Returns(Task.CompletedTask);

        var dto = await Sut().CreateAsync(Request(ScheduleType.TriggerBased), default);

        Assert.False(string.IsNullOrEmpty(dto.WebhookToken));
        Assert.NotEqual(dto.WebhookToken, saved!.WebhookTokenHash);
        Assert.True(WebhookTokens.Verify(dto.WebhookToken, saved.WebhookTokenHash));
        Assert.False(WebhookTokens.Verify("wrong", saved.WebhookTokenHash));
    }

    [Theory]
    [InlineData("report", "")]           // required missing
    [InlineData("email", "not-an-email")]
    [InlineData("tolerance", "abc")]     // number
    public async Task Create_InvalidConfig_Throws(string key, string value)
    {
        var config = GoodConfig();
        config[key] = value;
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(config: config), default));
    }

    [Fact]
    public async Task Create_UnknownConfigField_Throws()
    {
        var config = GoodConfig();
        config["surprise"] = "x";
        await Assert.ThrowsAsync<ValidationException>(() => Sut().CreateAsync(Request(config: config), default));
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(11, 30)]
    [InlineData(3, -1)]
    [InlineData(3, 3601)]
    public async Task Create_RetryPolicyOutOfBounds_Throws(int retries, int backoff) =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().CreateAsync(Request(retry: new RetryPolicyDto(retries, backoff)), default));

    // ---- visibility ----

    private static Job JobFor(Team team, Guid? owner = null) => new()
    {
        Team = team,
        OwnerId = owner ?? Guid.NewGuid(),
        Template = new JobTemplate { Name = "T" },
        Name = "j"
    };

    private JobQuery CaptureQuery()
    {
        JobQuery? captured = null;
        _jobs.Setup(j => j.ListAsync(It.IsAny<JobQuery>(), It.IsAny<CancellationToken>()))
            .Callback<JobQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(Array.Empty<Job>());
        Sut().ListAsync(new JobFilter(null, null, null, null, null, null), default).GetAwaiter().GetResult();
        return captured!;
    }

    [Fact]
    public void List_Employee_SeesOwnTeamOnly()
    {
        var q = CaptureQuery();
        Assert.Equal([Team.Business], q.VisibleTeams);
    }

    [Fact]
    public void List_Employee_AlsoSeesObserverTeams()
    {
        _me.SetupGet(m => m.Team).Returns(Team.Technical);
        _me.SetupGet(m => m.ObserverTeams).Returns([Team.Business]);

        var q = CaptureQuery();

        Assert.Equivalent(new[] { Team.Technical, Team.Business }, q.VisibleTeams!.ToArray());
    }

    [Fact]
    public void List_ViewOtherTeamsClaim_SeesEveryTeam()
    {
        _me.SetupGet(m => m.Permissions).Returns([Permissions.ViewOtherTeamsJobs]);
        Assert.Null(CaptureQuery().VisibleTeams);
    }

    [Fact]
    public void List_Admin_SeesEveryTeam()
    {
        _me.SetupGet(m => m.Role).Returns(Role.Admin);
        Assert.Null(CaptureQuery().VisibleTeams);
    }

    [Fact]
    public async Task Get_JobInInvisibleTeam_LooksLikeNotFound()
    {
        var job = JobFor(Team.Technical);
        _jobs.Setup(j => j.FindAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        await Assert.ThrowsAsync<NotFoundException>(() => Sut().GetAsync(job.Id, default));
    }

    // ---- cancel ----

    [Fact]
    public async Task Cancel_ByOwner_SetsCancelled()
    {
        var job = JobFor(Team.Business, _myId);
        _jobs.Setup(j => j.FindAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        var dto = await Sut().CancelAsync(job.Id, default);

        Assert.Equal(JobStatus.Cancelled, dto.Status);
        _jobs.Verify(j => j.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _scheduler.Verify(s => s.UnscheduleAsync(job.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_ByNonOwnerEmployee_Forbidden()
    {
        var job = JobFor(Team.Business);
        _jobs.Setup(j => j.FindAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().CancelAsync(job.Id, default));
        Assert.Equal(JobStatus.Scheduled, job.Status);
    }

    [Fact]
    public async Task Cancel_ByAdmin_Allowed()
    {
        _me.SetupGet(m => m.Role).Returns(Role.Admin);
        var job = JobFor(Team.Technical);
        _jobs.Setup(j => j.FindAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        Assert.Equal(JobStatus.Cancelled, (await Sut().CancelAsync(job.Id, default)).Status);
    }

    [Theory]
    [InlineData(JobStatus.InProgress)]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Cancelled)]
    [InlineData(JobStatus.Failed)]
    public async Task Cancel_JobInTerminalOrRunningState_Conflict(JobStatus status)
    {
        var job = JobFor(Team.Business, _myId);
        job.Status = status;
        _jobs.Setup(j => j.FindAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        await Assert.ThrowsAsync<ConflictException>(() => Sut().CancelAsync(job.Id, default));
    }

    [Fact]
    public async Task Cancel_NeedsManualAction_IsAllowed()
    {
        var job = JobFor(Team.Business, _myId);
        job.Status = JobStatus.NeedsManualAction;
        _jobs.Setup(j => j.FindAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);

        Assert.Equal(JobStatus.Cancelled, (await Sut().CancelAsync(job.Id, default)).Status);
    }
}

public class IstTests
{
    [Fact]
    public void ToUtc_SubtractsFiveAndAHalfHours() =>
        Assert.Equal(new DateTime(2026, 1, 1, 18, 30, 0), Ist.ToUtc(new DateTime(2026, 1, 2, 0, 0, 0)));

    [Fact]
    public void FromUtc_RoundTrips_AndIsUnspecifiedKind()
    {
        var utc = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
        var ist = Ist.FromUtc(utc);

        Assert.Equal(DateTimeKind.Unspecified, ist.Kind);
        Assert.Equal(new DateTime(2026, 6, 15, 15, 30, 0), ist);
        Assert.Equal(utc, Ist.ToUtc(ist));
    }
}
