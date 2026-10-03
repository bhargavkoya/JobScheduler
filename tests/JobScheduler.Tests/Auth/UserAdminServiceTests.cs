using JobScheduler.Application.Auth;
using JobScheduler.Domain.Users;
using Moq;

namespace JobScheduler.Tests.Auth;

public class UserAdminServiceTests
{
    private readonly Mock<IUserStore> _users = new();
    private readonly UserAdminService _sut;
    private readonly User _user = new() { Email = "a@b.com" };

    public UserAdminServiceTests()
    {
        _users.Setup(u => u.FindByIdAsync(_user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_user);
        _sut = new UserAdminService(_users.Object);
    }

    [Fact]
    public async Task UpdateTeam_ChangesTeamRoleAndObservers_ExcludingPrimaryFromObservers()
    {
        var profile = await _sut.UpdateTeamAsync(_user.Id,
            new UpdateTeamRequest(Team.Technical, Role.Admin, [Team.Business, Team.Technical]), default);

        Assert.Equal(Team.Technical, profile.PrimaryTeam);
        Assert.Equal(Role.Admin, profile.Role);
        Assert.Equal([Team.Business], profile.ObserverTeams);
        _users.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateClaims_ReplacesPermissions()
    {
        _user.SetPermissions([Permissions.RetryJobs, Permissions.ApproveJobs]);

        var profile = await _sut.UpdateClaimsAsync(_user.Id,
            new UpdateClaimsRequest([Permissions.ApproveJobs, Permissions.ViewOtherTeamsJobs]), default);

        Assert.Equal([Permissions.ApproveJobs, Permissions.ViewOtherTeamsJobs], profile.Permissions);
    }

    [Fact]
    public async Task UpdateClaims_UnknownPermission_ThrowsAndDoesNotSave()
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.UpdateClaimsAsync(_user.Id, new UpdateClaimsRequest(["jobs.delete.everything"]), default));

        _users.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateTeam_UnknownUser_Throws()
    {
        await Assert.ThrowsAsync<UserNotFoundException>(() =>
            _sut.UpdateTeamAsync(Guid.NewGuid(), new UpdateTeamRequest(Team.Business, Role.Employee, null), default));
    }
}
