using CloudHealthOffice.FieldProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SponsorService.Security;

/// <summary>
/// Any action that needs to encrypt or decrypt a protected field when field
/// protection is unusable (no key ring configured, or a value that does not
/// decrypt) answers 503 instead of 500, and nothing is stored in plaintext.
/// </summary>
public sealed class FieldProtectionExceptionFilter : IExceptionFilter
{
    private readonly ILogger<FieldProtectionExceptionFilter> _logger;

    public FieldProtectionExceptionFilter(ILogger<FieldProtectionExceptionFilter> logger) => _logger = logger;

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not FieldProtectionException ex) return;

        _logger.LogError(ex, "A protected field could not be encrypted or decrypted on {Path}",
            context.HttpContext.Request.Path.Value?.Replace("\r", string.Empty).Replace("\n", string.Empty));
        context.Result = new ObjectResult(new ProblemDetails
        {
            Title = "Encryption unavailable",
            Detail = ex.Message,
            Status = StatusCodes.Status503ServiceUnavailable,
        })
        { StatusCode = StatusCodes.Status503ServiceUnavailable };
        context.ExceptionHandled = true;
    }
}
