using JobScheduler.Application.Auth;
using JobScheduler.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace JobScheduler.Api;

/// <summary>Maps Application exceptions to ProblemDetails so controllers stay thin.</summary>
public class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is ValidationException or DuplicateEmailException
                                       or InvalidCredentialsException or UserNotFoundException
                                       or NotFoundException or ForbiddenException or ConflictException)
        {
            var status = ex switch
            {
                ValidationException => StatusCodes.Status400BadRequest,
                DuplicateEmailException or ConflictException => StatusCodes.Status409Conflict,
                ForbiddenException => StatusCodes.Status403Forbidden,
                InvalidCredentialsException => StatusCodes.Status401Unauthorized,
                _ => StatusCodes.Status404NotFound
            };
            logger.LogInformation("Request failed with {Status}: {Message}", status, ex.Message);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Detail = ex.Message });
        }
    }
}
