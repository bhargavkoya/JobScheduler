using System.Collections.Concurrent;
using JobScheduler.Application.Runs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JobScheduler.Api.Controllers;

public class FinanceStubOptions
{
    public const string SectionName = "FinanceStub";

    /// <summary>Demo switch: the first N trigger calls per idempotency key answer 503, to exercise retries.</summary>
    public int FailFirstAttempts { get; set; }
}

public class MockFinanceState
{
    public ConcurrentDictionary<string, int> Calls { get; } = new();
    public ConcurrentDictionary<string, string> ReportIds { get; } = new();
}

public sealed record TriggerReportRequest(string IdempotencyKey, string ReportName);
public sealed record TriggerReportResponse(string ReportId);

/// <summary>Stand-in for the internal finance app's REST API. Development only.</summary>
[ApiController]
[AllowAnonymous]
[Route("mock-finance")]
public class MockFinanceController(
    IWebHostEnvironment env, IOptionsMonitor<FinanceStubOptions> options, MockFinanceState state) : ControllerBase
{
    [HttpPost("reports")]
    public ActionResult<TriggerReportResponse> Trigger(TriggerReportRequest request)
    {
        if (!env.IsDevelopment()) return NotFound();

        var calls = state.Calls.AddOrUpdate(request.IdempotencyKey, 1, (_, n) => n + 1);
        var failFirst = options.CurrentValue.FailFirstAttempts;
        if (calls <= failFirst)
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { detail = $"Simulated finance app outage (call {calls} of {failFirst})." });

        // Idempotent: the same key always yields the same report.
        var id = state.ReportIds.GetOrAdd(request.IdempotencyKey, _ => Guid.NewGuid().ToString("N"));
        return Ok(new TriggerReportResponse(id));
    }

    [HttpGet("reports/{id}")]
    public ActionResult<ReportData> Get(string id)
    {
        if (!env.IsDevelopment()) return NotFound();
        if (!state.ReportIds.Values.Contains(id)) return NotFound();

        return Ok(new ReportData(id,
        [
            new ReportRow("ACC-001", 1250.50m),
            new ReportRow("ACC-002", -300.00m),
            new ReportRow("ACC-003", 98.25m)
        ]));
    }
}
