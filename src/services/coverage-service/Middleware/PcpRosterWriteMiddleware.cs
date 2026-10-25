using System.Threading.Tasks;
using CoverageService.Exceptions;
using CoverageService.Services;
using Microsoft.AspNetCore.Http;

namespace CoverageService.Middleware;

/// <summary>Explicit retry/exception responses for every coverage writer, including 834 clients.</summary>
public sealed class PcpRosterWriteMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (PcpRosterWriteException ex)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { Code = ex.Code, Message = ex.Message });
        }
        catch (CoverageConcurrencyException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { Code = "COVERAGE_CONCURRENCY_CONFLICT", Message = "Coverage changed concurrently; reload and retry." });
        }
        catch (PanelCountUnavailableException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { Code = "PCP_PANEL_UNAVAILABLE", Message = "PCP roster transaction is unavailable; retry without dropping the enrollment exception." });
        }
        catch (ProviderDirectoryUnavailableException)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { Code = "PROVIDER_DIRECTORY_UNAVAILABLE", Message = "Provider validation is unavailable; retry without dropping the enrollment exception." });
        }
    }
}
