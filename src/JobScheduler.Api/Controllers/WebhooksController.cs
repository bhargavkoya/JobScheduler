using JobScheduler.Application.Auth;
using JobScheduler.Application.Runs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

/// <summary>Inbound webhook for TriggerBased jobs. Anonymous: the per-job secret in X-Webhook-Token is the credential.</summary>
[ApiController]
[AllowAnonymous]
[Route("webhooks")]
public class WebhooksController(ITriggerService triggers) : ControllerBase
{
    [HttpPost("jobs/{id:guid}")]
    public async Task<ActionResult<JobRunDto>> Trigger(
        Guid id,
        [FromHeader(Name = "X-Webhook-Token")] string? token,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var payload = await ReadBodyAsync(ct);
        return Accepted(await triggers.TriggerAsync(id, token, idempotencyKey, payload, ct));
    }

    /// <summary>Reads at most one character over the limit, so an oversized body is rejected without buffering all of it.</summary>
    private async Task<string> ReadBodyAsync(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var buffer = new char[TriggerService.MaxPayloadLength + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        if (read > TriggerService.MaxPayloadLength)
            throw new ValidationException($"Webhook payload is too large (max {TriggerService.MaxPayloadLength} characters).");
        return new string(buffer, 0, read);
    }
}
