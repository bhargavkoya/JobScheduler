using JobScheduler.Application.Jobs;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

[ApiController]
[Authorize]
[Route("jobs")]
public class JobsController(IJobService jobs) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<JobDto>> Create(CreateJobRequest request, CancellationToken ct)
    {
        var job = await jobs.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = job.Id }, job);
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<JobDto>>> List(
        [FromQuery] JobStatus? status,
        [FromQuery] Team? team,
        [FromQuery] ScheduleType? type,
        [FromQuery] Guid? ownerId,
        [FromQuery] DateTime? createdFromUtc,
        [FromQuery] DateTime? createdToUtc,
        CancellationToken ct) =>
        Ok(await jobs.ListAsync(new JobFilter(status, team, type, ownerId, createdFromUtc, createdToUtc), ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<JobDto>> Get(Guid id, CancellationToken ct) => Ok(await jobs.GetAsync(id, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<JobDto>> Cancel(Guid id, CancellationToken ct) => Ok(await jobs.CancelAsync(id, ct));
}
