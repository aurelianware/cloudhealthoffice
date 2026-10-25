using CoverageService.Exceptions;
using CoverageService.Middleware;
using CoverageService.Models;
using CoverageService.Services;
using Microsoft.AspNetCore.Http;

namespace CoverageService.Tests.Services;

public class PcpRosterWriteMiddlewareTests
{
    [Theory]
    [InlineData("full", 409, "PANEL_FULL")]
    [InlineData("direct", 409, "PCP_ASSIGNMENT_API_REQUIRED")]
    [InlineData("concurrent", 409, "COVERAGE_CONCURRENCY_CONFLICT")]
    [InlineData("store", 503, "PCP_PANEL_UNAVAILABLE")]
    [InlineData("provider", 503, "PROVIDER_DIRECTORY_UNAVAILABLE")]
    public async Task EnrollmentWriterGetsExplicitFailure_NotSuccess(string failure, int status, string code)
    {
        Exception error = failure switch
        {
            "full" => new PcpRosterWriteException(PcpValidationCodes.PanelFull, "full"),
            "direct" => new PcpRosterWriteException("PCP_ASSIGNMENT_API_REQUIRED", "direct"),
            "concurrent" => new CoverageConcurrencyException("synthetic", 1),
            "store" => new PanelCountUnavailableException(),
            _ => new ProviderDirectoryUnavailableException("synthetic", 503),
        };
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new PcpRosterWriteMiddleware(_ => Task.FromException(error)).InvokeAsync(context);
        context.Response.StatusCode.Should().Be(status);
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).Should().Contain(code);
    }
}
