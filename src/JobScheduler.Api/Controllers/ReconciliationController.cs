using JobScheduler.Application.Reconciliation;
using JobScheduler.Domain.Reconciliation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

/// <summary>A file reconciliation job's setup, and the record-level results of its runs.</summary>
[ApiController]
[Authorize]
public class ReconciliationController(IReconciliationSetupService setup, IReconciliationResultsService results) : ControllerBase
{
    /// <summary>The job's current files (with their columns) and rules.</summary>
    [HttpGet("jobs/{jobId:guid}/reconciliation")]
    public async Task<ActionResult<ReconciliationSetupViewDto>> Get(Guid jobId, CancellationToken ct) =>
        Ok(await setup.GetAsync(jobId, ct));

    /// <summary>Swap the file behind an alias (for example today's extract). The old file is kept, marked superseded.</summary>
    [HttpPut("jobs/{jobId:guid}/reconciliation/files")]
    public async Task<ActionResult<ReconciliationSetupViewDto>> ReplaceFile(
        Guid jobId, ReplaceFileRequest request, CancellationToken ct) =>
        Ok(await setup.ReplaceFileAsync(jobId, request, ct));

    /// <summary>Totals per rule for one run.</summary>
    [HttpGet("runs/{runId:guid}/reconciliation/summary")]
    public async Task<ActionResult<RunReconciliationSummaryDto>> Summary(Guid runId, CancellationToken ct) =>
        Ok(await results.GetSummaryAsync(runId, ct));

    /// <summary>One row per record per rule. Filter with ?status=Pass|Fail and ?ruleId=; page with ?page=&amp;pageSize=.</summary>
    [HttpGet("runs/{runId:guid}/reconciliation/results")]
    public async Task<ActionResult<ResultsPageDto>> Results(
        Guid runId, [FromQuery] RecordStatus? status, [FromQuery] Guid? ruleId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = ReconciliationResultsService.DefaultPageSize,
        CancellationToken ct = default) =>
        Ok(await results.ListAsync(runId, status, ruleId, page, pageSize, ct));

    /// <summary>Every matching record as CSV (default) or PDF: ?format=csv|pdf, optionally ?status= and ?ruleId=.</summary>
    [HttpGet("runs/{runId:guid}/reconciliation/export")]
    public async Task<FileContentResult> Export(
        Guid runId, [FromQuery] string? format, [FromQuery] RecordStatus? status, [FromQuery] Guid? ruleId,
        CancellationToken ct)
    {
        var file = await results.ExportAsync(runId, format, status, ruleId, ct);
        return File(file.Content, file.ContentType, $"reconciliation-{runId}.{file.FileExtension}");
    }
}
