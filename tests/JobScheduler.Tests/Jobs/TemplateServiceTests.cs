using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using Moq;

namespace JobScheduler.Tests.Jobs;

public class TemplateServiceTests
{
    private readonly Mock<ITemplateStore> _store = new();
    private readonly TemplateService _sut;

    public TemplateServiceTests()
    {
        _sut = new TemplateService(_store.Object);
    }

    private static SaveTemplateRequest Valid() => new(
        "New template",
        "desc",
        [ScheduleType.Manual],
        [new TemplateFieldDto("amount", "Amount", FieldType.Number, true)],
        null);

    [Fact]
    public async Task Create_SavesUnapprovedTemplate_WithDefaultRetry()
    {
        JobTemplate? saved = null;
        _store.Setup(s => s.AddAsync(It.IsAny<JobTemplate>(), It.IsAny<CancellationToken>()))
            .Callback<JobTemplate, CancellationToken>((t, _) => saved = t).Returns(Task.CompletedTask);

        var dto = await _sut.CreateAsync(Valid(), default);

        Assert.False(saved!.IsApproved);
        Assert.False(dto.IsApproved);
        Assert.Equal(3, saved.DefaultRetryPolicy.MaxAutoRetries);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_DuplicateName_Conflict()
    {
        _store.Setup(s => s.NameExistsAsync("New template", null, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        await Assert.ThrowsAsync<ConflictException>(() => _sut.CreateAsync(Valid(), default));
    }

    [Fact]
    public async Task Create_DuplicateFieldNames_Throws()
    {
        var request = Valid() with
        {
            Fields =
            [
                new TemplateFieldDto("a", "A", FieldType.String, true),
                new TemplateFieldDto("A", "Again", FieldType.String, true)
            ]
        };
        await Assert.ThrowsAsync<ValidationException>(() => _sut.CreateAsync(request, default));
    }

    [Fact]
    public async Task Create_NoScheduleTypes_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.CreateAsync(Valid() with { SupportedScheduleTypes = [] }, default));

    [Fact]
    public async Task Create_BlankName_Throws() =>
        await Assert.ThrowsAsync<ValidationException>(() => _sut.CreateAsync(Valid() with { Name = "  " }, default));

    [Fact]
    public async Task Update_ResetsApproval()
    {
        var existing = new JobTemplate { Name = "Old", IsApproved = true };
        _store.Setup(s => s.FindAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var dto = await _sut.UpdateAsync(existing.Id, Valid(), default);

        Assert.False(dto.IsApproved);
        Assert.Equal("New template", existing.Name);
    }

    [Fact]
    public async Task Update_UnknownTemplate_NotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateAsync(Guid.NewGuid(), Valid(), default));

    [Fact]
    public async Task Approve_SetsApproved()
    {
        var existing = new JobTemplate { Name = "T", IsApproved = false };
        _store.Setup(s => s.FindAsync(existing.Id, It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var dto = await _sut.ApproveAsync(existing.Id, default);

        Assert.True(dto.IsApproved);
        _store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
