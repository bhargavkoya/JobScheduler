using JobScheduler.Domain.Users;
using Microsoft.AspNetCore.Authorization;

namespace JobScheduler.Infrastructure.Auth;

public static class PolicyNames
{
    public const string AdminOnly = "AdminOnly";
    public const string CanApprove = "CanApprove";
    public const string CanRetry = "CanRetry";
    public const string CanViewOtherTeams = "CanViewOtherTeams";
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>Admins implicitly hold every permission; employees need the explicit claim.</summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = context.User;
        if (user.HasClaim(AppClaimTypes.Role, nameof(Role.Admin)) ||
            user.HasClaim(AppClaimTypes.Permission, requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class AuthorizationExtensions
{
    public static AuthorizationOptions AddAppPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(PolicyNames.AdminOnly, p => p.RequireClaim(AppClaimTypes.Role, nameof(Role.Admin)));
        options.AddPolicy(PolicyNames.CanApprove, p => p.AddRequirements(new PermissionRequirement(Permissions.ApproveJobs)));
        options.AddPolicy(PolicyNames.CanRetry, p => p.AddRequirements(new PermissionRequirement(Permissions.RetryJobs)));
        options.AddPolicy(PolicyNames.CanViewOtherTeams, p => p.AddRequirements(new PermissionRequirement(Permissions.ViewOtherTeamsJobs)));
        return options;
    }
}
