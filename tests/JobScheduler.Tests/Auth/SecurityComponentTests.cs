using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using JobScheduler.Domain.Users;
using JobScheduler.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace JobScheduler.Tests.Auth;

public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _sut = new();

    [Fact]
    public void Hash_ThenVerify_Succeeds_AndWrongPasswordFails()
    {
        var hash = _sut.Hash("Passw0rd!");

        Assert.True(_sut.Verify("Passw0rd!", hash));
        Assert.False(_sut.Verify("passw0rd!", hash));
    }

    [Fact]
    public void Hash_UsesRandomSalt()
    {
        Assert.NotEqual(_sut.Hash("same"), _sut.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1.abc.x.y")]
    [InlineData("v1.1000.!!!.???")]
    public void Verify_MalformedHash_ReturnsFalse(string hash)
    {
        Assert.False(_sut.Verify("x", hash));
    }
}

public class JwtTokenServiceTests
{
    [Fact]
    public void CreateToken_ContainsRoleTeamObserverAndPermissionClaims_AndExpiresInConfiguredMinutes()
    {
        var options = Options.Create(new JwtOptions { Key = new string('k', 40), ExpiryMinutes = 60 });
        var sut = new JwtTokenService(options, TimeProvider.System);
        var user = new User { Email = "a@b.com", Role = Role.Employee, PrimaryTeam = Team.Technical };
        user.SetObserverTeams([Team.Business]);
        user.SetPermissions([Permissions.RetryJobs, Permissions.ApproveJobs]);

        var result = sut.CreateToken(user);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Token);

        Assert.Equal(user.Id.ToString(), jwt.Subject);
        Assert.Equal("Employee", jwt.Claims.Single(c => c.Type == AppClaimTypes.Role).Value);
        Assert.Equal("Technical", jwt.Claims.Single(c => c.Type == AppClaimTypes.Team).Value);
        Assert.Equal(["Business"], jwt.Claims.Where(c => c.Type == AppClaimTypes.ObserverTeam).Select(c => c.Value));
        Assert.Equivalent(new[] { Permissions.RetryJobs, Permissions.ApproveJobs },
            jwt.Claims.Where(c => c.Type == AppClaimTypes.Permission).Select(c => c.Value).ToArray());
        Assert.InRange((result.ExpiresAtUtc - DateTime.UtcNow).TotalMinutes, 59, 61);
    }
}

public class PermissionAuthorizationHandlerTests
{
    private static async Task<bool> Authorize(string permission, params Claim[] claims)
    {
        var requirement = new PermissionRequirement(permission);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        var context = new AuthorizationHandlerContext([requirement], principal, null);
        await new PermissionAuthorizationHandler().HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Admin_PassesWithoutExplicitClaim() =>
        Assert.True(await Authorize(Permissions.ApproveJobs, new Claim(AppClaimTypes.Role, "Admin")));

    [Fact]
    public async Task Employee_WithClaim_Passes() =>
        Assert.True(await Authorize(Permissions.ApproveJobs,
            new Claim(AppClaimTypes.Role, "Employee"), new Claim(AppClaimTypes.Permission, Permissions.ApproveJobs)));

    [Fact]
    public async Task Employee_WithoutClaim_IsDenied() =>
        Assert.False(await Authorize(Permissions.ApproveJobs, new Claim(AppClaimTypes.Role, "Employee")));

    [Fact]
    public async Task Employee_WithDifferentClaim_IsDenied() =>
        Assert.False(await Authorize(Permissions.ApproveJobs,
            new Claim(AppClaimTypes.Role, "Employee"), new Claim(AppClaimTypes.Permission, Permissions.RetryJobs)));
}
