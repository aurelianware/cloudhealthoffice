using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Controllers;

/// <summary>
/// Unexpected failures answer with a problem and a trace id; the exception
/// text (which can carry connection strings, hosts or PHI) stays in the log.
/// </summary>
public class UnexpectedErrorResponseTests
{
    private const string Secret = "mongodb://admin:hunter2@db:27017";

    [Fact]
    public async Task Credentialing_publish_failure_is_a_503_problem_without_the_exception_text()
    {
        var service = Substitute.For<ICredentialingService>();
        service.SubmitApplicationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SubmitApplicationRequest>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(Secret));
        var controller = new CredentialingController(service, NullLogger<CredentialingController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = TestHelpers.TokenHttpContext.For("tenant-a") },
        };

        var result = await controller.SubmitApplication(
            "p-001", new SubmitApplicationRequest { ApplicationSource = "Manual" }, CancellationToken.None);

        var obj = result.Result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(503);
        var problem = obj.Value.Should().BeOfType<ProblemDetails>().Subject;
        JsonSerializer.Serialize(problem).Should().NotContain("hunter2");
        problem.Extensions.Should().ContainKey("traceId");
    }

    [Fact]
    public async Task Mpip_bulk_import_row_failure_reports_a_fixed_error_without_the_exception_text()
    {
        var service = Substitute.For<IMpipRateService>();
        service.UpsertQualificationAsync(Arg.Any<MpipProviderQualification>())
            .ThrowsAsync(new InvalidOperationException(Secret));
        var controller = new MpipController(service, NullLogger<MpipController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = TestHelpers.TokenHttpContext.For("tenant-a") },
        };

        var result = await controller.BulkImport(
            "tenant-a", new List<MpipProviderQualification> { new() { ProviderId = "p-001" } });

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<BulkImportResult>().Subject;
        body.Errors.Should().ContainSingle()
            .Which.Should().Contain(MpipController.BulkImportRowFailedMessage)
            .And.NotContain("hunter2");
    }
}
