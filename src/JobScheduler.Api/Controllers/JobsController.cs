using JobScheduler.Application.Jobs;
using JobScheduler.Application.Runs;
using JobScheduler.Infrastructure.Auth;
using JobScheduler.Domain.Jobs;
using JobScheduler.Domain.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

[ApiController]
[Authorize]
[Route("jobs")]
public class JobsController(IJobService jobs, IJobRunService runs) : ControllerBase
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

    /// <summary>Manual kickoff. Send an Idempotency-Key header to make double-clicks and client retries safe.</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<ActionResult<JobRunDto>> Run(
        Guid id, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct) =>
        Accepted(await runs.RunNowAsync(id, idempotencyKey, ct));

    [Authorize(Policy = PolicyNames.CanRetry)]
    [HttpPost("{id:guid}/retry")]
    public async Task<ActionResult<JobRunDto>> Retry(Guid id, CancellationToken ct) =>
        Accepted(await runs.RetryAsync(id, ct));

    [HttpGet("{id:guid}/runs/export")]
    public async Task<FileContentResult> ExportRuns(Guid id, CancellationToken ct) =>
        File(System.Text.Encoding.UTF8.GetBytes(await runs.ExportRunsCsvAsync(id, ct)), "text/csv", $"job-{id}-runs.csv");

    [HttpGet("{id:guid}/runs")]
    public async Task<ActionResult<IReadOnlyList<JobRunDto>>> Runs(Guid id, CancellationToken ct) =>
        Ok(await runs.ListRunsAsync(id, ct));
}
