using JobScheduler.Application.Auth;
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
                                       or InvalidCredentialsException or UserNotFoundException)
        {
            var status = ex switch
            {
                ValidationException => StatusCodes.Status400BadRequest,
                DuplicateEmailException => StatusCodes.Status409Conflict,
                InvalidCredentialsException => StatusCodes.Status401Unauthorized,
                _ => StatusCodes.Status404NotFound
            };
            logger.LogInformation("Request failed with {Status}: {Message}", status, ex.Message);
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Detail = ex.Message });
        }
    }
}
