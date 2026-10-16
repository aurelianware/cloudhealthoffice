using CloudHealthOffice.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Middleware;

/// <summary>
/// Answers a <see cref="ConcurrencyConflictException"/> (a conditional write
/// that lost to a concurrent one) with 409 Conflict and a problem body, in
/// every service: <c>UseChoAuthentication</c> installs it. Other exceptions
/// pass through untouched.
/// </summary>
public sealed class ConcurrencyConflictMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ConcurrencyConflictMiddleware> _logger;

    public ConcurrencyConflictMiddleware(RequestDelegate next, ILogger<ConcurrencyConflictMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ConcurrencyConflictException ex) when (!context.Response.HasStarted)
        {
            _logger.LogInformation(ex, "Concurrent write conflict on {Path} (trace id {TraceId})",
                context.Request.Path, ChoProblemResults.TraceId(context));
            context.Response.Clear();
            await ChoProblemResults.ServerErrorResult(context,
                    "The resource was changed by another request. Re-read it and retry.",
                    StatusCodes.Status409Conflict)
                .ExecuteAsync(context);
        }
    }
}
