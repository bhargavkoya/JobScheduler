using JobScheduler.Application.Approvals;
using JobScheduler.Application.Jobs;
using JobScheduler.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

[ApiController]
[Authorize]
[Route("jobs")]
public class ApprovalsController(IApprovalService approvals) : ControllerBase
{
    /// <summary>Every visible job currently waiting on a human, oldest first.</summary>
    [HttpGet("manual-queue")]
    public async Task<ActionResult<IReadOnlyList<ManualActionItemDto>>> Queue(CancellationToken ct) =>
        Ok(await approvals.ListQueueAsync(ct));

    [HttpGet("{id:guid}/approvals")]
    public async Task<ActionResult<IReadOnlyList<ApprovalDto>>> History(Guid id, CancellationToken ct) =>
        Ok(await approvals.ListForJobAsync(id, ct));

    [Authorize(Policy = PolicyNames.CanApprove)]
    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<JobDto>> Approve(Guid id, DecisionRequest? request, CancellationToken ct) =>
        Ok(await approvals.ApproveAsync(id, request?.Comment, ct));

    [Authorize(Policy = PolicyNames.CanApprove)]
    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<JobDto>> Reject(Guid id, DecisionRequest? request, CancellationToken ct) =>
        Ok(await approvals.RejectAsync(id, request?.Comment, ct));
}
