using JobScheduler.Application.Auth;
using JobScheduler.Application.Runs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Runs;
using JobScheduler.Domain.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JobScheduler.Tests.Runs;

public class IdempotentEmailSenderTests
{
    private readonly Mock<IEmailTransport> _transport = new();
    private readonly Mock<ISentEmailStore> _store = new();
    private readonly Mock<TimeProvider> _clock = new();

    public IdempotentEmailSenderTests()
    {
        _clock.Setup(c => c.GetUtcNow()).Returns(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        _store.Setup(s => s.TryRecordAsync(It.IsAny<SentEmail>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private IdempotentEmailSender Sut() =>
        new(_transport.Object, _store.Object, _clock.Object, NullLogger<IdempotentEmailSender>.Instance);

    private static EmailMessage Message(string key = "k1") => new("a@b.com", "Subject", "Body", key);

    [Fact]
    public async Task NewKey_SendsThenRecords()
    {
        await Sut().SendAsync(Message(), default);

        _transport.Verify(t => t.SendAsync("a@b.com", "Subject", "Body", It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.TryRecordAsync(
            It.Is<SentEmail>(e => e.IdempotencyKey == "k1" && e.To == "a@b.com"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KeyAlreadySent_SkipsTheSend()
    {
        _store.Setup(s => s.ExistsAsync("k1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await Sut().SendAsync(Message(), default);

        _transport.VerifyNoOtherCalls();
        _store.Verify(s => s.TryRecordAsync(It.IsAny<SentEmail>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TransportFails_RecordsNothing_SoTheRetryCanStillSend()
    {
        _transport.Setup(t => t.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().SendAsync(Message(), default));

        _store.Verify(s => s.TryRecordAsync(It.IsAny<SentEmail>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LostRecordRace_DoesNotThrow()
    {
        _store.Setup(s => s.TryRecordAsync(It.IsAny<SentEmail>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Sut().SendAsync(Message(), default);
    }
}

public class OwnerFailureNotifierTests
{
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IEmailSender> _email = new();

    private readonly User _owner = new() { Email = "owner@x.com" };
    private readonly Job _job;
    private readonly JobRun _run;

    public OwnerFailureNotifierTests()
    {
        _job = new Job { Name = "Recon", OwnerId = _owner.Id };
        _run = new JobRun
        {
            JobId = _job.Id, IdempotencyKey = "run-key", Attempt = 3,
            FailedStep = PipelineStep.Calculate, Error = "boom"
        };
        _users.Setup(u => u.FindByIdAsync(_owner.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_owner);
    }

    private OwnerFailureNotifier Sut() => new(_users.Object, _email.Object, NullLogger<OwnerFailureNotifier>.Instance);

    [Fact]
    public async Task EmailsTheOwner_WithStepAndError()
    {
        await Sut().NotifyFailedAsync(_job, _run, default);

        _email.Verify(e => e.SendAsync(
            It.Is<EmailMessage>(m => m.To == "owner@x.com"
                                     && m.Subject.Contains("Recon")
                                     && m.Body.Contains("Calculate")
                                     && m.Body.Contains("boom")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task KeyIsPerRunAttempt_SoRedeliveryDedupesButANewFailureNotifies()
    {
        var keys = new List<string>();
        _email.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => keys.Add(m.IdempotencyKey)).Returns(Task.CompletedTask);

        await Sut().NotifyFailedAsync(_job, _run, default);
        await Sut().NotifyFailedAsync(_job, _run, default);
        _run.Attempt = 4;
        await Sut().NotifyFailedAsync(_job, _run, default);

        Assert.Equal(["run-key:failure:3", "run-key:failure:3", "run-key:failure:4"], keys);
    }

    [Fact]
    public async Task MissingOwner_SendsNothing()
    {
        _users.Setup(u => u.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        await Sut().NotifyFailedAsync(_job, _run, default);

        _email.VerifyNoOtherCalls();
    }
}
