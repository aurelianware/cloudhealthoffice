using EligibilityService.Controllers;
using EligibilityService.Models;
using EligibilityService.Repositories;
using EligibilityService.Services;
using CloudHealthOffice.Infrastructure.Edi.Interchange;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.EligibilityService.Tests;

/// <summary>
/// Unexpected failures answer with a problem and a trace id; the exception
/// text (which can carry connection strings, hosts or PHI) stays in the log.
/// The 270 parser's own validation messages still reach the caller.
/// </summary>
public class EligibilityErrorResponseTests
{
    private const string Secret = "mongodb://admin:hunter2@db:27017";

    private static (EligibilityController Controller, IEligibilityService Service, IEdi270Parser Parser) Build(string? body = null)
    {
        var service = Substitute.For<IEligibilityService>();
        var parser = Substitute.For<IEdi270Parser>();
        var controller = new EligibilityController(
            service,
            Substitute.For<IEligibilityRepository>(),
            parser,
            Substitute.For<IEdi271Generator>(),
            NullLogger<EligibilityController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "t1";
        if (body is not null)
            http.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, service, parser);
    }

    private static IRealTime270Service RealTime(IEligibilityService service, IEdi270Parser parser) =>
        new RealTime270Service(service, parser, Substitute.For<IEdi271Generator>(), NullLogger<RealTime270Service>.Instance);

    [Fact]
    public async Task Inquiry_UnexpectedFailure_IsAProblem_WithoutTheExceptionText()
    {
        var (controller, service, _) = Build();
        service.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>()).ThrowsAsync(new InvalidOperationException(Secret));

        var result = await controller.SubmitInquiry(new EligibilityInquiry { SubscriberId = "M1" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(500, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.DoesNotContain("hunter2", problem.Detail);
        Assert.DoesNotContain("hunter2", problem.Title);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task Inquiry_CoverageFailure_Is503RatherThanAnEligibilityResponse()
    {
        var (controller, service, _) = Build();
        service.ProcessInquiryAsync(Arg.Any<EligibilityInquiry>()).ThrowsAsync(
            new CoverageLookupUnavailableException(new HttpRequestException(Secret)));

        var result = await controller.SubmitInquiry(new EligibilityInquiry { SubscriberId = "M1" });

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, obj.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(obj.Value);
        Assert.DoesNotContain("hunter2", problem.Detail);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task QuickCheck_CoverageFailure_Is503RatherThanFalseIneligibility()
    {
        var (controller, service, _) = Build();
        service.QuickEligibilityCheckAsync("t1", "M1", null, Arg.Any<DateTime>()).ThrowsAsync(
            new CoverageLookupUnavailableException(new HttpRequestException(Secret)));

        var result = await controller.QuickCheck("M1");

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, obj.StatusCode);
        Assert.IsType<ProblemDetails>(obj.Value);
    }

    [Fact]
    public async Task Edi270_UnexpectedParserFailure_Is400_WithoutTheExceptionText()
    {
        var (controller, service, parser) = Build("ISA*00*...");
        parser.Parse(Arg.Any<string>()).Throws(new NullReferenceException(Secret));

        var result = await controller.ProcessEdi270(RealTime(service, parser));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.DoesNotContain("hunter2", bad.Value!.ToString());
    }

    [Fact]
    public async Task Edi270_ParserValidationMessage_StillReachesTheCaller()
    {
        var (controller, service, parser) = Build("ISA*00*...");
        parser.Parse(Arg.Any<string>()).Throws(new FormatException("EDI content is too short to contain a valid ISA segment."));

        var result = await controller.ProcessEdi270(RealTime(service, parser));

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("too short", bad.Value!.ToString());
    }
}
