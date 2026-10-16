using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CloudHealthOffice.Infrastructure.Middleware;

/// <summary>
/// Error responses for unexpected failures. The client gets an RFC 7807
/// problem with a generic detail and a <c>traceId</c>. The exception text
/// (connection strings, driver messages, document ids, PHI) never leaves
/// the service, and callers log the exception themselves. The trace id is the
/// one <see cref="ExceptionHandlingMiddleware"/> reports and the logs carry,
/// so support can find the logged exception from it.
/// </summary>
public static class ChoProblemResults
{
    public const string GenericDetail = "An unexpected error occurred. Quote the traceId when reporting it.";

    /// <summary>The correlation id for the current request.</summary>
    public static string TraceId(HttpContext? context) =>
        Activity.Current?.Id ?? context?.TraceIdentifier ?? string.Empty;

    /// <summary>A problem response for a controller action.</summary>
    public static ObjectResult ServerError(
        this ControllerBase controller, string title, int statusCode = StatusCodes.Status500InternalServerError) =>
        new(Build(controller.HttpContext, title, statusCode))
        {
            StatusCode = statusCode,
            ContentTypes = { "application/problem+json" },
        };

    /// <summary>A problem response for a minimal-API endpoint.</summary>
    public static IResult ServerErrorResult(
        HttpContext context, string title, int statusCode = StatusCodes.Status500InternalServerError) =>
        Results.Problem(Build(context, title, statusCode));

    private static ProblemDetails Build(HttpContext? context, string title, int statusCode)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = GenericDetail,
        };
        problem.Extensions["traceId"] = TraceId(context);
        return problem;
    }
}
