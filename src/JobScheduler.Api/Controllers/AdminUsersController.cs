using JobScheduler.Application.Auth;
using JobScheduler.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

[ApiController]
[Route("admin/users")]
[Authorize(Policy = PolicyNames.AdminOnly)]
public class AdminUsersController(IUserAdminService admin) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserProfile>>> List(CancellationToken ct) =>
        Ok(await admin.ListUsersAsync(ct));

    [HttpPut("{id:guid}/team")]
    public async Task<ActionResult<UserProfile>> UpdateTeam(Guid id, UpdateTeamRequest request, CancellationToken ct) =>
        Ok(await admin.UpdateTeamAsync(id, request, ct));

    [HttpPut("{id:guid}/claims")]
    public async Task<ActionResult<UserProfile>> UpdateClaims(Guid id, UpdateClaimsRequest request, CancellationToken ct) =>
        Ok(await admin.UpdateClaimsAsync(id, request, ct));
}
