using JobScheduler.Application.Jobs;
using JobScheduler.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

[ApiController]
[Authorize]
public class TemplatesController(ITemplateService templates) : ControllerBase
{
    /// <summary>Approved templates for everyone; admins can also list unapproved ones.</summary>
    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<TemplateDto>>> List([FromQuery] bool includeUnapproved, CancellationToken ct)
    {
        var admin = User.HasClaim(AppClaimTypes.Role, "Admin");
        return Ok(await templates.ListAsync(includeUnapproved && admin, ct));
    }

    [Authorize(Policy = PolicyNames.AdminOnly)]
    [HttpPost("admin/templates")]
    public async Task<ActionResult<TemplateDto>> Create(SaveTemplateRequest request, CancellationToken ct) =>
        Ok(await templates.CreateAsync(request, ct));

    [Authorize(Policy = PolicyNames.AdminOnly)]
    [HttpPut("admin/templates/{id:guid}")]
    public async Task<ActionResult<TemplateDto>> Update(Guid id, SaveTemplateRequest request, CancellationToken ct) =>
        Ok(await templates.UpdateAsync(id, request, ct));

    [Authorize(Policy = PolicyNames.AdminOnly)]
    [HttpPost("admin/templates/{id:guid}/approve")]
    public async Task<ActionResult<TemplateDto>> Approve(Guid id, CancellationToken ct) =>
        Ok(await templates.ApproveAsync(id, ct));
}
