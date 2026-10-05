using System.Text;
using System.Text.Json;
using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Application.Notifications;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Runs;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JobScheduler.Tests.Runs;

public class WebhookTokensTests
{
    [Fact]
    public void Generate_IsLongRandomAndDifferentEachTime()
    {
        var a = WebhookTokens.Generate();
        var b = WebhookTokens.Generate();

        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Verify_AcceptsTheRightToken_AndRejectsEverythingElse()
    {
        var token = WebhookTokens.Generate();
        var hash = WebhookTokens.Hash(token);

        Assert.NotEqual(token, hash);
        Assert.True(WebhookTokens.Verify(token, hash));
        Assert.False(WebhookTokens.Verify(WebhookTokens.Generate(), hash));
        Assert.False(WebhookTokens.Verify(token.ToUpperInvariant(), hash));
        Assert.False(WebhookTokens.Verify(null, hash));
        Assert.False(WebhookTokens.Verify("", hash));
        Assert.False(WebhookTokens.Verify(token, null));
    }
}

public class TriggerServiceTests
{
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IRunOrchestrator> _orchestrator = new();
    private readonly string _token = WebhookTokens.Generate();
    private readonly Job _job;

    public TriggerServiceTests()
    {
        _job = new Job { Name = "hook", ScheduleType = ScheduleType.TriggerBased, WebhookTokenHash = WebhookTokens.Hash(_token) };
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
        _orchestrator.Setup(o => o.EnqueueTriggeredAsync(
                _job.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, string? key, string? payload, CancellationToken _) =>
                new JobRun { JobId = id, IdempotencyKey = $"{id}:trigger:{key}", TriggerPayload = payload });
    }

    private TriggerService Sut() => new(_jobs.Object, _orchestrator.Object);

    [Fact]
    public async Task ValidToken_QueuesARun_PassingTheKeyAndPayload()
    {
        var run = await Sut().TriggerAsync(_job.Id, _token, "key-1", """{"event":"ready"}""", default);

        Assert.Equal("""{"event":"ready"}""", run.TriggerPayload);
        _orchestrator.Verify(o => o.EnqueueTriggeredAsync(
            _job.Id, "key-1", """{"event":"ready"}""", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BlankPayload_IsStoredAsNull()
    {
        await Sut().TriggerAsync(_job.Id, _token, null, "  ", default);

        _orchestrator.Verify(o => o.EnqueueTriggeredAsync(_job.Id, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("")]
    [InlineData(null)]
    public async Task WrongOrMissingToken_Is401_AndQueuesNothing(string? token)
    {
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Sut().TriggerAsync(_job.Id, token, null, null, default));

        _orchestrator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnknownJob_LooksTheSameAsAWrongToken()
    {
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Sut().TriggerAsync(Guid.NewGuid(), _token, null, null, default));
    }

    [Fact]
    public async Task NonTriggerJob_EvenWithAMatchingHash_Is401()
    {
        _job.ScheduleType = ScheduleType.Manual;

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => Sut().TriggerAsync(_job.Id, _token, null, null, default));
        _orchestrator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OversizedPayload_IsRejected()
    {
        var payload = new string('x', TriggerService.MaxPayloadLength + 1);

        await Assert.ThrowsAsync<ValidationException>(() => Sut().TriggerAsync(_job.Id, _token, null, payload, default));
        _orchestrator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PayloadAtTheLimit_IsAccepted()
    {
        var payload = new string('x', TriggerService.MaxPayloadLength);

        var run = await Sut().TriggerAsync(_job.Id, _token, null, payload, default);

        Assert.Equal(payload, run.TriggerPayload);
    }
}

public class TriggeredRunOrchestrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IJobQueue> _queue = new();
    private readonly Mock<TimeProvider> _clock = new();
    private readonly Job _job = new() { Name = "hook", ScheduleType = ScheduleType.TriggerBased, Status = JobStatus.Scheduled };

    public TriggeredRunOrchestrationTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(Now);
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
    }

    private RunOrchestrator Sut() => new(_jobs.Object, _runs.Object, _queue.Object, _clock.Object);

    [Fact]
    public async Task Trigger_CreatesPendingRun_WithPayload_AndEnqueuesOnce()
    {
        JobRun? added = null;
        _runs.Setup(r => r.AddAsync(It.IsAny<JobRun>(), It.IsAny<CancellationToken>()))
            .Callback<JobRun, CancellationToken>((r, _) => added = r).Returns(Task.CompletedTask);

        var run = await Sut().EnqueueTriggeredAsync(_job.Id, "abc", "body", default);

        Assert.Same(added, run);
        Assert.Equal($"{_job.Id}:trigger:abc", run.IdempotencyKey);
        Assert.Equal("body", run.TriggerPayload);
        Assert.Null(run.TriggeredByUserId);
        Assert.Equal(JobStatus.InProgress, _job.Status);
        _queue.Verify(q => q.EnqueueAsync(run.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Trigger_SameKey_ReturnsTheExistingRun_WithoutEnqueuingAgain()
    {
        var existing = new JobRun { JobId = _job.Id, IdempotencyKey = $"{_job.Id}:trigger:abc" };
        _runs.Setup(r => r.FindByKeyAsync(existing.IdempotencyKey, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var run = await Sut().EnqueueTriggeredAsync(_job.Id, "abc", "again", default);

        Assert.Same(existing, run);
        _queue.Verify(q => q.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()), Times.Never);
        _runs.Verify(r => r.AddAsync(It.IsAny<JobRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Trigger_WithoutAKey_EveryCallIsANewRun()
    {
        var keys = new List<string>();
        _runs.Setup(r => r.AddAsync(It.IsAny<JobRun>(), It.IsAny<CancellationToken>()))
            .Callback<JobRun, CancellationToken>((r, _) => keys.Add(r.IdempotencyKey)).Returns(Task.CompletedTask);

        await Sut().EnqueueTriggeredAsync(_job.Id, null, "same body", default);
        _job.Status = JobStatus.Scheduled; // pretend the first run finished and the job is ready again
        await Sut().EnqueueTriggeredAsync(_job.Id, null, "same body", default);

        Assert.Equal(2, keys.Distinct().Count());
    }

    [Fact]
    public async Task Trigger_WhileTheJobIsBusy_IsAConflict()
    {
        _job.Status = JobStatus.InProgress;

        await Assert.ThrowsAsync<ConflictException>(() => Sut().EnqueueTriggeredAsync(_job.Id, "k", null, default));
    }

    [Fact]
    public async Task Trigger_OnANonTriggerJob_IsRejected()
    {
        _job.ScheduleType = ScheduleType.Manual;

        await Assert.ThrowsAsync<ValidationException>(() => Sut().EnqueueTriggeredAsync(_job.Id, "k", null, default));
    }

    [Fact]
    public async Task Trigger_KeyIsTrimmedAndCapped()
    {
        JobRun? added = null;
        _runs.Setup(r => r.AddAsync(It.IsAny<JobRun>(), It.IsAny<CancellationToken>()))
            .Callback<JobRun, CancellationToken>((r, _) => added = r).Returns(Task.CompletedTask);

        await Sut().EnqueueTriggeredAsync(_job.Id, "  " + new string('k', 500) + "  ", null, default);

        Assert.Equal($"{_job.Id}:trigger:{new string('k', 100)}", added!.IdempotencyKey);
    }
}

public class TriggerBasedLifecycleTests
{
    [Fact]
    public void CompletedTriggerJob_IsReadyAgain_LikeARecurrentOne()
    {
        var job = new Job { ScheduleType = ScheduleType.TriggerBased, Status = JobStatus.Completed };

        Assert.True(job.CanStartRun);
        Assert.True(job.CanCancel);
    }

    [Fact]
    public void FailedTriggerJob_IsNotReady_UntilRetried()
    {
        var job = new Job { ScheduleType = ScheduleType.TriggerBased, Status = JobStatus.Failed };

        Assert.False(job.CanStartRun);
    }

    [Theory]
    [InlineData(ScheduleType.Fixed)]
    [InlineData(ScheduleType.Manual)]
    [InlineData(ScheduleType.EventBased)]
    public void OneShotJobs_AreNotReadyAfterCompleting(ScheduleType type)
    {
        Assert.False(new Job { ScheduleType = type, Status = JobStatus.Completed }.CanStartRun);
    }
}

public class PreferenceAwareEmailSenderTests
{
    private readonly Mock<IEmailSender> _inner = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<INotificationPreferenceStore> _prefs = new();
    private readonly User _user = new() { Email = "ann@x.com" };

    public PreferenceAwareEmailSenderTests()
    {
        _users.Setup(u => u.FindByEmailAsync("ann@x.com", It.IsAny<CancellationToken>())).ReturnsAsync(_user);
    }

    private PreferenceAwareEmailSender Sut() =>
        new(_inner.Object, _users.Object, _prefs.Object, NullLogger<PreferenceAwareEmailSender>.Instance);

    private static EmailMessage Message(NotificationEvent? evt, string to = "ann@x.com") => new(to, "S", "B", "key", evt);

    private void Pref(NotificationEvent evt, bool enabled) =>
        _prefs.Setup(p => p.FindAsync(_user.Id, evt, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPreference { UserId = _user.Id, Event = evt, Enabled = enabled });

    [Fact]
    public async Task MutedEvent_IsSkipped_AndNothingIsSentOrRecorded()
    {
        Pref(NotificationEvent.JobFailed, enabled: false);

        await Sut().SendAsync(Message(NotificationEvent.JobFailed), default);

        _inner.Verify(i => i.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnabledEvent_IsSent()
    {
        Pref(NotificationEvent.JobFailed, enabled: true);
        var message = Message(NotificationEvent.JobFailed);

        await Sut().SendAsync(message, default);

        _inner.Verify(i => i.SendAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoPreferenceRow_MeansEnabled()
    {
        var message = Message(NotificationEvent.JobCompleted);

        await Sut().SendAsync(message, default);

        _inner.Verify(i => i.SendAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MutingOneEvent_DoesNotMuteAnother()
    {
        Pref(NotificationEvent.JobCompleted, enabled: false);
        var failed = Message(NotificationEvent.JobFailed);

        await Sut().SendAsync(failed, default);

        _inner.Verify(i => i.SendAsync(failed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UntaggedEmail_IgnoresPreferences_AndSkipsTheLookup()
    {
        var message = Message(null);

        await Sut().SendAsync(message, default);

        _inner.Verify(i => i.SendAsync(message, It.IsAny<CancellationToken>()), Times.Once);
        _users.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RecipientWhoIsNotAUser_AlwaysGetsTheMail()
    {
        var message = Message(NotificationEvent.JobFailed, "stranger@x.com");

        await Sut().SendAsync(message, default);

        _inner.Verify(i => i.SendAsync(message, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecipientLookup_IgnoresCaseAndWhitespace()
    {
        Pref(NotificationEvent.FollowUpReminder, enabled: false);

        await Sut().SendAsync(Message(NotificationEvent.FollowUpReminder, "  ANN@X.com "), default);

        _inner.Verify(i => i.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class NotificationPreferenceServiceTests
{
    private readonly Mock<INotificationPreferenceStore> _store = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Guid _myId = Guid.NewGuid();
    private readonly List<NotificationPreference> _rows = [];

    public NotificationPreferenceServiceTests()
    {
        _me.SetupGet(m => m.UserId).Returns(_myId);
        _store.Setup(s => s.ListForUserAsync(_myId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _rows.ToList());
        _store.Setup(s => s.FindAsync(_myId, It.IsAny<NotificationEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, NotificationEvent e, CancellationToken _) => _rows.FirstOrDefault(r => r.Event == e));
        _store.Setup(s => s.AddAsync(It.IsAny<NotificationPreference>(), It.IsAny<CancellationToken>()))
            .Callback<NotificationPreference, CancellationToken>((p, _) => _rows.Add(p)).Returns(Task.CompletedTask);
    }

    private NotificationPreferenceService Sut() => new(_store.Object, _me.Object);

    [Fact]
    public async Task Get_ForANewUser_ListsEveryEvent_AllEnabled()
    {
        var prefs = await Sut().GetMineAsync(default);

        Assert.Equal(Enum.GetValues<NotificationEvent>().Length, prefs.Count);
        Assert.All(prefs, p => Assert.True(p.Enabled));
    }

    [Fact]
    public async Task Save_StoresTheChoice_ForTheCallerOnly()
    {
        var result = await Sut().SaveMineAsync(
            new SaveNotificationPreferencesRequest([new NotificationPreferenceDto(NotificationEvent.JobFailed, false)]), default);

        var row = Assert.Single(_rows);
        Assert.Equal(_myId, row.UserId);
        Assert.False(row.Enabled);
        Assert.False(result.Single(p => p.Event == NotificationEvent.JobFailed).Enabled);
        Assert.True(result.Single(p => p.Event == NotificationEvent.JobCompleted).Enabled);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_AgainUpdatesTheExistingRow_InsteadOfAddingADuplicate()
    {
        var sut = Sut();
        await sut.SaveMineAsync(new SaveNotificationPreferencesRequest([new(NotificationEvent.JobFailed, false)]), default);
        await sut.SaveMineAsync(new SaveNotificationPreferencesRequest([new(NotificationEvent.JobFailed, true)]), default);

        var row = Assert.Single(_rows);
        Assert.True(row.Enabled);
    }

    [Fact]
    public async Task Save_DuplicateEventsInOneRequest_LastOneWins()
    {
        await Sut().SaveMineAsync(new SaveNotificationPreferencesRequest(
            [new(NotificationEvent.JobFailed, false), new(NotificationEvent.JobFailed, true)]), default);

        Assert.True(Assert.Single(_rows).Enabled);
    }

    [Fact]
    public async Task Save_UnknownEvent_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() => Sut().SaveMineAsync(
            new SaveNotificationPreferencesRequest([new((NotificationEvent)99, false)]), default));
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Save_WithoutPreferences_IsRejected()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().SaveMineAsync(new SaveNotificationPreferencesRequest(null), default));
    }
}

public class EmailEventTaggingTests
{
    private static Job JobWith(string configJson, bool requiresApproval = false) => new()
    {
        Name = "Recon",
        ConfigJson = configJson,
        Template = new JobTemplate
        {
            RequiresApproval = requiresApproval,
            Fields = [new TemplateField { Name = "to", Type = FieldType.Email }]
        }
    };

    [Theory]
    [InlineData(false, NotificationEvent.JobCompleted)]
    [InlineData(true, NotificationEvent.ApprovalRequested)]
    public async Task ResultEmail_IsTaggedWithTheMatchingEvent(bool approval, NotificationEvent expected)
    {
        var sender = new Mock<IEmailSender>();
        var ctx = new StepContext(JobWith("""{"to":"a@x.com"}""", approval), new JobRun { IdempotencyKey = "k" },
            new Dictionary<PipelineStep, string>());

        await new SendEmailStep(sender.Object).ExecuteAsync(ctx, default);

        sender.Verify(s => s.SendAsync(It.Is<EmailMessage>(m => m.Event == expected), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailureEmail_IsTaggedJobFailed()
    {
        var owner = new User { Email = "owner@x.com" };
        var users = new Mock<IUserStore>();
        users.Setup(u => u.FindByIdAsync(owner.Id, It.IsAny<CancellationToken>())).ReturnsAsync(owner);
        var sender = new Mock<IEmailSender>();
        var job = new Job { Name = "j", OwnerId = owner.Id };

        await new OwnerFailureNotifier(users.Object, sender.Object, NullLogger<OwnerFailureNotifier>.Instance)
            .NotifyFailedAsync(job, new JobRun { IdempotencyKey = "k", Attempt = 1 }, default);

        sender.Verify(s => s.SendAsync(
            It.Is<EmailMessage>(m => m.Event == NotificationEvent.JobFailed && m.To == "owner@x.com"), It.IsAny<CancellationToken>()), Times.Once);
    }
}

public class OwnerCompletionNotifierTests
{
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly User _owner = new() { Email = "owner@x.com" };

    public OwnerCompletionNotifierTests()
    {
        _users.Setup(u => u.FindByIdAsync(_owner.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_owner);
    }

    private OwnerCompletionNotifier Sut() => new(_users.Object, _email.Object, NullLogger<OwnerCompletionNotifier>.Instance);

    private Job JobMailingTo(string? recipient) => new()
    {
        Name = "Recon",
        OwnerId = _owner.Id,
        ConfigJson = recipient is null ? "{}" : JsonSerializer.Serialize(new Dictionary<string, string> { ["to"] = recipient }),
        Template = new JobTemplate { Fields = [new TemplateField { Name = "to", Type = FieldType.Email }] }
    };

    private static JobRun Run() => new() { IdempotencyKey = "run-key", Attempt = 2 };

    [Fact]
    public async Task Completed_MailsTheOwner_TaggedAndKeyedPerRun()
    {
        await Sut().NotifyCompletedAsync(JobMailingTo("someone.else@x.com"), Run(), default);

        _email.Verify(e => e.SendAsync(
            It.Is<EmailMessage>(m => m.To == "owner@x.com" && m.IdempotencyKey == "run-key:completed"
                                     && m.Event == NotificationEvent.JobCompleted && m.Subject.Contains("completed")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResultEmailAlreadyGoesToTheOwner_NoSecondEmail_EvenWithDifferentCase()
    {
        await Sut().NotifyCompletedAsync(JobMailingTo("OWNER@x.com"), Run(), default);

        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task JobWithNoRecipient_StillNotifiesTheOwner()
    {
        await Sut().NotifyCompletedAsync(JobMailingTo(null), Run(), default);

        _email.Verify(e => e.SendAsync(It.Is<EmailMessage>(m => m.To == "owner@x.com"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OwnerNoLongerExists_SendsNothing_AndDoesNotThrow()
    {
        var job = JobMailingTo(null);
        job.OwnerId = Guid.NewGuid();

        await Sut().NotifyCompletedAsync(job, Run(), default);

        _email.VerifyNoOtherCalls();
    }
}

public class RunHistoryExportTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);

    private static JobDto JobDto() => new(
        Guid.NewGuid(), Guid.NewGuid(), "Daily Recon", "My job", ScheduleType.Manual, null, null, JobStatus.Completed,
        Team.Business, Guid.NewGuid(), new RetryPolicyDto(1, 1), new Dictionary<string, string>(), T0, false, false,
        null, T0, false, null);

    private static JobRunDto Run(string key, string? error = null, string? payload = null) => new(
        Guid.NewGuid(), Guid.NewGuid(), key, error is null ? RunStatus.Succeeded : RunStatus.Failed, 1, 0, error,
        error is null ? null : PipelineStep.Calculate, T0, T0, T0.AddSeconds(5),
        [new RunStepDto(PipelineStep.DownloadReport, StepStatus.Succeeded, 1, "ok", T0, T0.AddSeconds(1))], payload);

    private static RunHistoryReport Report(params JobRunDto[] runs) => new(JobDto(), "owner@x.com", runs);

    [Fact]
    public void CsvExporter_ProducesTheCsvFile_WithTriggerPayloadColumn()
    {
        var file = new CsvRunHistoryExporter().Export(Report(Run("k1", payload: "{\"event\":\"x\"}")));
        var text = Encoding.UTF8.GetString(file.Content);

        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal("csv", file.FileExtension);
        Assert.EndsWith("TriggerPayload\r\n", text.Split("\r\n")[0] + "\r\n");
        Assert.Contains("\"{\"\"event\"\":\"\"x\"\"}\"", text);
    }

    [Fact]
    public void PdfExporter_ProducesAPdfDocument()
    {
        var file = new PdfRunHistoryExporter().Export(Report(Run("k1"), Run("k2", error: "boom")));

        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal("pdf", file.FileExtension);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(file.Content, 0, 4));
        Assert.True(file.Content.Length > 1000);
    }

    [Fact]
    public void PdfExporter_JobWithNoRuns_StillProducesAPdf()
    {
        var file = new PdfRunHistoryExporter().Export(Report());

        Assert.Equal("%PDF", Encoding.ASCII.GetString(file.Content, 0, 4));
    }

    [Fact]
    public void Exporters_AdvertiseDistinctFormats()
    {
        Assert.Equal("csv", new CsvRunHistoryExporter().Format);
        Assert.Equal("pdf", new PdfRunHistoryExporter().Format);
    }
}

public class JobRunServiceExportTests
{
    private readonly Mock<IJobStore> _jobs = new();
    private readonly Mock<IJobRunStore> _runs = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<ICurrentUser> _me = new();
    private readonly Mock<IRunHistoryExporter> _csv = new();
    private readonly Mock<IRunHistoryExporter> _pdf = new();
    private readonly Guid _myId = Guid.NewGuid();
    private readonly Job _job;
    private readonly ExportedFile _csvFile = new([1], "text/csv", "csv");
    private readonly ExportedFile _pdfFile = new([2], "application/pdf", "pdf");

    public JobRunServiceExportTests()
    {
        _me.SetupGet(m => m.UserId).Returns(_myId);
        _me.SetupGet(m => m.Role).Returns(Role.Employee);
        _me.SetupGet(m => m.Team).Returns(Team.Business);
        _me.SetupGet(m => m.ObserverTeams).Returns(Array.Empty<Team>());
        _me.SetupGet(m => m.Permissions).Returns(Array.Empty<string>());

        _job = new Job { Name = "j", Team = Team.Business, OwnerId = _myId, Template = new JobTemplate { Name = "T" } };
        _jobs.Setup(j => j.FindAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_job);
        _runs.Setup(r => r.ListForJobAsync(_job.Id, It.IsAny<CancellationToken>())).ReturnsAsync([new JobRun { JobId = _job.Id }]);
        _users.Setup(u => u.FindByIdAsync(_myId, It.IsAny<CancellationToken>())).ReturnsAsync(new User { Email = "me@x.com" });

        _csv.SetupGet(e => e.Format).Returns("csv");
        _csv.Setup(e => e.Export(It.IsAny<RunHistoryReport>())).Returns(_csvFile);
        _pdf.SetupGet(e => e.Format).Returns("pdf");
        _pdf.Setup(e => e.Export(It.IsAny<RunHistoryReport>())).Returns(_pdfFile);
    }

    private JobRunService Sut() => new(
        _jobs.Object, _runs.Object, new Mock<IRunOrchestrator>().Object, _me.Object,
        [_csv.Object, _pdf.Object], _users.Object, TimeProvider.System, AtRiskPolicy.Default);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("csv")]
    [InlineData(" CSV ")]
    public async Task NoFormatOrCsv_ReturnsTheCsvFile(string? format)
    {
        Assert.Same(_csvFile, await Sut().ExportRunsAsync(_job.Id, format, default));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("PDF")]
    public async Task Pdf_ReturnsThePdfFile_WithJobHeaderAndOwnerEmail(string format)
    {
        var file = await Sut().ExportRunsAsync(_job.Id, format, default);

        Assert.Same(_pdfFile, file);
        _pdf.Verify(e => e.Export(It.Is<RunHistoryReport>(r =>
            r.Job.Name == "j" && r.OwnerEmail == "me@x.com" && r.Runs.Count == 1)), Times.Once);
    }

    [Fact]
    public async Task UnknownFormat_IsAValidationError_ListingTheSupportedOnes()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Sut().ExportRunsAsync(_job.Id, "xlsx", default));

        Assert.Contains("csv", ex.Message);
        Assert.Contains("pdf", ex.Message);
    }

    [Fact]
    public async Task JobInAnotherTeam_LooksMissing()
    {
        _job.Team = Team.Technical;

        await Assert.ThrowsAsync<NotFoundException>(() => Sut().ExportRunsAsync(_job.Id, "pdf", default));
        _pdf.Verify(e => e.Export(It.IsAny<RunHistoryReport>()), Times.Never);
    }
}
