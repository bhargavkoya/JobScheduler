using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using JobScheduler.Application.Runs;
using JobScheduler.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api.Controllers;

public sealed record EmailTestResult(string To, string Message);

/// <summary>
/// Lets an admin confirm that Gmail is really wired up. It mails only the calling admin's own address (no free-text
/// recipient, so it cannot be used to spam anyone) and bypasses the idempotency record so it can be repeated.
/// </summary>
[ApiController]
[Authorize(Policy = PolicyNames.AdminOnly)]
[Route("admin/email")]
public class AdminEmailController(
    ICurrentUser me, IUserStore users, ILogger<AdminEmailController> log, IEmailTransport? transport = null) : ControllerBase
{
    [HttpPost("test")]
    public async Task<ActionResult<EmailTestResult>> Test(CancellationToken ct)
    {
        if (transport is null)
            throw new ConflictException(
                "Gmail is not configured, so emails are only logged. Set Email:Username and Email:Password (user-secrets or env vars) and restart.");

        var admin = await users.FindByIdAsync(me.UserId, ct) ?? throw new NotFoundException("Your user record was not found.");
        try
        {
            await transport.SendAsync(
                admin.Email,
                "[Job Scheduler] Gmail test",
                $"This is a test message from the Job Scheduler, requested by {admin.Email} at {DateTime.UtcNow:u}.\n\nIf you can read this, Gmail SMTP is wired up correctly.",
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Gmail test send failed.");
            return StatusCode(StatusCodes.Status502BadGateway,
                new ProblemDetails { Status = StatusCodes.Status502BadGateway, Detail = $"Gmail rejected or could not send the message: {ex.Message}" });
        }

        return Ok(new EmailTestResult(admin.Email, "Test email sent. Check the inbox (and spam) of that address."));
    }
}
