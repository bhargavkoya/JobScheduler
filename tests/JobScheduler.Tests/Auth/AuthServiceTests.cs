using JobScheduler.Application.Auth;
using JobScheduler.Domain.Users;
using Moq;

namespace JobScheduler.Tests.Auth;

public class AuthServiceTests
{
    private readonly Mock<IUserStore> _users = new();
    private readonly Mock<IPasswordHasher> _hasher = new();
    private readonly Mock<ITokenService> _tokens = new();
    private readonly AuthService _sut;

    public AuthServiceTests()
    {
        _hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns<string>(p => "hash:" + p);
        _hasher.Setup(h => h.Verify(It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string>((p, h) => h == "hash:" + p);
        _tokens.Setup(t => t.CreateToken(It.IsAny<User>()))
            .Returns(new TokenResult("jwt", DateTime.UtcNow.AddHours(1)));
        _sut = new AuthService(_users.Object, _hasher.Object, _tokens.Object);
    }

    [Fact]
    public async Task Register_CreatesBusinessEmployeeWithNoClaims_AndNormalizesEmail()
    {
        _users.Setup(u => u.FindByEmailAsync("a@b.com", It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        User? saved = null;
        _users.Setup(u => u.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()))
            .Callback<User, CancellationToken>((u, _) => saved = u).Returns(Task.CompletedTask);

        var result = await _sut.RegisterAsync(new RegisterRequest("  A@B.com ", "password1"), default);

        Assert.NotNull(saved);
        Assert.Equal("a@b.com", saved!.Email);
        Assert.Equal(Role.Employee, saved.Role);
        Assert.Equal(Team.Business, saved.PrimaryTeam);
        Assert.Empty(saved.Claims);
        Assert.Equal("hash:password1", saved.PasswordHash);
        Assert.Equal("jwt", result.Token);
        _users.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Throws_AndDoesNotSave()
    {
        _users.Setup(u => u.FindByEmailAsync("a@b.com", It.IsAny<CancellationToken>())).ReturnsAsync(new User { Email = "a@b.com" });

        await Assert.ThrowsAsync<DuplicateEmailException>(() =>
            _sut.RegisterAsync(new RegisterRequest("a@b.com", "password1"), default));

        _users.Verify(u => u.AddAsync(It.IsAny<User>(), It.IsAny<CancellationToken>()), Times.Never);
        _users.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("not-an-email", "password1")]
    [InlineData("", "password1")]
    [InlineData("a@b.com", "short")]
    [InlineData("a@b.com", "")]
    public async Task Register_InvalidInput_Throws(string email, string password)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _sut.RegisterAsync(new RegisterRequest(email, password), default));
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsToken()
    {
        var user = new User { Email = "a@b.com", PasswordHash = "hash:password1" };
        _users.Setup(u => u.FindByEmailAsync("a@b.com", It.IsAny<CancellationToken>())).ReturnsAsync(user);

        var result = await _sut.LoginAsync(new LoginRequest("A@b.com", "password1"), default);

        Assert.Equal("jwt", result.Token);
        Assert.Equal("a@b.com", result.User.Email);
    }

    [Fact]
    public async Task Login_WrongPassword_Throws_AndIssuesNoToken()
    {
        var user = new User { Email = "a@b.com", PasswordHash = "hash:password1" };
        _users.Setup(u => u.FindByEmailAsync("a@b.com", It.IsAny<CancellationToken>())).ReturnsAsync(user);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            _sut.LoginAsync(new LoginRequest("a@b.com", "wrong"), default));

        _tokens.Verify(t => t.CreateToken(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task Login_UnknownUser_ThrowsSameErrorAsWrongPassword()
    {
        _users.Setup(u => u.FindByEmailAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
            _sut.LoginAsync(new LoginRequest("ghost@b.com", "password1"), default));
    }

    [Fact]
    public async Task GetProfile_UnknownUser_Throws()
    {
        _users.Setup(u => u.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<UserNotFoundException>(() => _sut.GetProfileAsync(Guid.NewGuid(), default));
    }
}
