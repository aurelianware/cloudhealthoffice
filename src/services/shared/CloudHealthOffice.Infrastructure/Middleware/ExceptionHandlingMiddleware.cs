using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Middleware;

/// <summary>
/// Global exception handler that catches unhandled exceptions and returns a <see cref="StandardErrorResponse"/>.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, code) = MapException(exception);

        _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        var error = new StandardErrorResponse
        {
            Code = code,
            Message = _environment.IsDevelopment() ? exception.Message : GetPublicMessage(code),
            Details = _environment.IsDevelopment() ? exception.ToString() : null,
            TraceId = traceId
        };

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(error, JsonOptions));
    }

    private static (HttpStatusCode statusCode, string code) MapException(Exception exception) => exception switch
    {
        TenantContextMissingException => (HttpStatusCode.Unauthorized, "TENANT_CONTEXT_MISSING"),
        ArgumentException => (HttpStatusCode.BadRequest, "BAD_REQUEST"),
        UnauthorizedAccessException => (HttpStatusCode.Forbidden, "FORBIDDEN"),
        KeyNotFoundException => (HttpStatusCode.NotFound, "NOT_FOUND"),
        InvalidOperationException => (HttpStatusCode.InternalServerError, "INVALID_OPERATION"),
        NotImplementedException => (HttpStatusCode.NotImplemented, "NOT_IMPLEMENTED"),
        TimeoutException => (HttpStatusCode.GatewayTimeout, "TIMEOUT"),
        _ => (HttpStatusCode.InternalServerError, "INTERNAL_ERROR")
    };

    private static string GetPublicMessage(string code) => code switch
    {
        "TENANT_CONTEXT_MISSING" => "Tenant context is required but was not provided.",
        "BAD_REQUEST" => "The request was invalid.",
        "FORBIDDEN" => "Access denied.",
        "NOT_FOUND" => "The requested resource was not found.",
        "INVALID_OPERATION" => "An unexpected error occurred.",
        "NOT_IMPLEMENTED" => "This feature is not yet available.",
        "TIMEOUT" => "The request timed out.",
        _ => "An unexpected error occurred."
    };
}
