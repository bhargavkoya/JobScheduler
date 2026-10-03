using JobScheduler.Application.Calculation;
using JobScheduler.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

[ApiController]
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("admin/rules")]
public class RulesController(IRuleService rules) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RuleDto>>> List(CancellationToken ct) => Ok(await rules.ListAsync(ct));

    [HttpGet("types")]
    public ActionResult<IReadOnlyList<string>> Types() => Ok(rules.RuleTypes());

    [HttpPost]
    public async Task<ActionResult<RuleDto>> Create(SaveRuleRequest request, CancellationToken ct) =>
        Ok(await rules.CreateAsync(request, ct));

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RuleDto>> Update(Guid id, SaveRuleRequest request, CancellationToken ct) =>
        Ok(await rules.UpdateAsync(id, request, ct));
}
