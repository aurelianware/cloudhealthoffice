using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Hl7.Fhir.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class BulkExportControllerTests
{
    private static BulkExportController Controller(IBulkExportService? service = null)
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = "tenant-a";
        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("sub", "prov-1")], "test"));
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("test.example.com");
        http.Request.Headers["Prefer"] = "respond-async";
        return new BulkExportController(service ?? new BulkExportService(), NullLogger<BulkExportController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultService_Returns501OutcomeWithoutJobLocationOrManifest(bool group)
    {
        var controller = Controller();
        var result = group
            ? await controller.GroupExport("group-1", null, null, null, default)
            : await controller.SystemExport("Patient,Coverage", null, null, default);

        var obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(501);
        obj.ContentTypes.Should().Contain("application/fhir+json");
        var outcome = obj.Value.Should().BeOfType<OperationOutcome>().Subject;
        outcome.Issue.Single().Code.Should().Be(OperationOutcome.IssueType.NotSupported);
        controller.Response.Headers.Should().NotContainKey("Content-Location");
        controller.Response.Headers.Should().NotContainKey("X-Progress");
    }

    [Fact]
    public async Task MissingPreferHeader_Remains400()
    {
        var controller = Controller();
        controller.Request.Headers.Remove("Prefer");
        (await controller.SystemExport(null, null, null, default)).Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task NoDefaultJobsCanBePolledOrCancelled()
    {
        var controller = Controller();
        (await controller.ExportStatus("unknown", default)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.CancelExport("unknown", default)).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task AnImplementedBackend_CanStillAcceptAndReturnARealManifest()
    {
        var service = new Mock<IBulkExportService>();
        var job = new BulkExportJob { JobId = "real-job", Status = BulkExportStatus.Complete, RequestedBy = "prov-1",
            Manifest = new BulkExportManifest { Output = new() { new BulkExportOutput {
                Type = "Patient", Url = "https://test.example.com/authorized/patients.ndjson", Count = 1 } } } };
        service.Setup(s => s.InitiateExportAsync(It.IsAny<BulkExportRequest>(), "tenant-a", default)).ReturnsAsync(job);
        service.Setup(s => s.GetJobStatusAsync("real-job", "tenant-a", default)).ReturnsAsync(job);
        var controller = Controller(service.Object);

        (await controller.SystemExport(null, null, null, default)).Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(202);
        controller.Response.Headers["Content-Location"].ToString().Should().EndWith("/$export-status/real-job");
        (await controller.ExportStatus("real-job", default)).Should().BeOfType<OkObjectResult>().Which.Value.Should().BeSameAs(job.Manifest);
    }

    [Fact]
    public async Task CancelledJobs_DoNotPollAsInProgressForever()
    {
        var service = new Mock<IBulkExportService>();
        service.Setup(s => s.GetJobStatusAsync("cancelled", "tenant-a", default)).ReturnsAsync(
            new BulkExportJob { JobId = "cancelled", Status = BulkExportStatus.Cancelled, RequestedBy = "prov-1" });
        (await Controller(service.Object).ExportStatus("cancelled", default)).Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(410);
    }

    [Fact]
    public async Task AJobAnotherCallerStarted_IsNotFound_AndNotCancelled()
    {
        var service = new Mock<IBulkExportService>();
        service.Setup(s => s.GetJobStatusAsync("theirs", "tenant-a", default)).ReturnsAsync(
            new BulkExportJob { JobId = "theirs", Status = BulkExportStatus.Complete, RequestedBy = "prov-2" });
        var controller = Controller(service.Object);

        (await controller.ExportStatus("theirs", default)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.CancelExport("theirs", default)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.ExportFile("theirs", "Patient-1.ndjson", default)).Should().BeOfType<NotFoundObjectResult>();
        service.Verify(s => s.CancelJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(s => s.OpenFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ACompletedJobWithoutAStoredManifest_IsListedFromItsFiles()
    {
        var service = new Mock<IBulkExportService>();
        var expires = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        service.Setup(s => s.GetJobStatusAsync("j1", "tenant-a", default)).ReturnsAsync(new BulkExportJob
        {
            JobId = "j1", Status = BulkExportStatus.Complete, RequestedBy = "prov-1", ExpiresAt = expires,
            Request = "https://test.example.com/fhir/r4/Group/prov-1/$export",
            Files = [new BulkExportFile { Type = "Patient", Name = "Patient-1.ndjson", Count = 3 }],
        });
        var controller = Controller(service.Object);

        var manifest = (await controller.ExportStatus("j1", default)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<BulkExportManifest>().Subject;
        manifest.RequiresAccessToken.Should().BeTrue();
        manifest.Output.Single().Url.Should().Be("https://test.example.com/fhir/r4/$export-file/j1/Patient-1.ndjson");
        manifest.Output.Single().Count.Should().Be(3);
        controller.Response.Headers["Expires"].ToString().Should().Be(expires.ToString("R"));
    }
}
