using FhirService.Controllers;
using FhirService.Models;
using FhirService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Controllers;

public class BulkExportControllerTests
{
    private readonly BulkExportService _exportService;
    private readonly BulkExportController _controller;

    public BulkExportControllerTests()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["Fhir:ServerBaseUrl"]).Returns("https://test.example.com/fhir/r4");
        var logger = new Mock<ILogger<BulkExportService>>();
        var controllerLogger = new Mock<ILogger<BulkExportController>>();

        _exportService = new BulkExportService(config.Object, logger.Object);
        _controller = new BulkExportController(_exportService, controllerLogger.Object);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["TenantId"] = "test-tenant";
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("test.example.com");
        httpContext.Request.Headers["Prefer"] = "respond-async";
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext,
        };
    }

    // ── System export ────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_ValidRequest_Returns202WithContentLocation()
    {
        var result = await _controller.SystemExport(null, null, null, CancellationToken.None);

        var statusResult = result.Should().BeOfType<StatusCodeResult>().Subject;
        statusResult.StatusCode.Should().Be(202);
        _controller.Response.Headers["Content-Location"].ToString()
            .Should().Contain("$export-status/export-");
    }

    [Fact]
    public async Task Export_MissingPreferHeader_Returns400()
    {
        // Remove the Prefer header
        _controller.HttpContext.Request.Headers.Remove("Prefer");

        var result = await _controller.SystemExport(null, null, null, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Export_WithTypeFilter_ReturnsFilteredManifest()
    {
        // Initiate export with type filter
        var exportResult = await _controller.SystemExport("Patient,Coverage", null, null, CancellationToken.None);
        exportResult.Should().BeOfType<StatusCodeResult>();

        // Extract jobId from Content-Location
        var contentLocation = _controller.Response.Headers["Content-Location"].ToString();
        var jobId = contentLocation.Split("$export-status/").Last();

        // Poll status
        var statusResult = await _controller.ExportStatus(jobId, CancellationToken.None);
        var okResult = statusResult.Should().BeOfType<OkObjectResult>().Subject;
        var manifest = okResult.Value.Should().BeOfType<BulkExportManifest>().Subject;

        manifest.Output.Should().HaveCount(2);
        manifest.Output.Should().Contain(o => o.Type == "Patient");
        manifest.Output.Should().Contain(o => o.Type == "Coverage");
    }

    // ── Group export ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GroupExport_ValidGroupId_Returns202()
    {
        var result = await _controller.GroupExport(
            "payer-exchange-001", null, null, null, CancellationToken.None);

        var statusResult = result.Should().BeOfType<StatusCodeResult>().Subject;
        statusResult.StatusCode.Should().Be(202);
        _controller.Response.Headers["Content-Location"].ToString()
            .Should().Contain("$export-status/");
    }

    // ── Export status ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportStatus_CompletedJob_Returns200WithManifest()
    {
        // Initiate
        await _controller.SystemExport(null, null, null, CancellationToken.None);
        var jobId = _controller.Response.Headers["Content-Location"].ToString()
            .Split("$export-status/").Last();

        // Reset response headers for the next call
        _controller.ControllerContext.HttpContext = new DefaultHttpContext();
        _controller.HttpContext.Items["TenantId"] = "test-tenant";
        _controller.HttpContext.Request.Scheme = "https";
        _controller.HttpContext.Request.Host = new HostString("test.example.com");

        var result = await _controller.ExportStatus(jobId, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var manifest = okResult.Value.Should().BeOfType<BulkExportManifest>().Subject;
        manifest.RequiresAccessToken.Should().BeTrue();
        manifest.Output.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ExportStatus_UnknownJobId_Returns404()
    {
        var result = await _controller.ExportStatus("nonexistent", CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);
    }

    // ── Cancel export ────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelExport_ExistingJob_Returns202()
    {
        // Initiate
        await _controller.SystemExport(null, null, null, CancellationToken.None);
        var jobId = _controller.Response.Headers["Content-Location"].ToString()
            .Split("$export-status/").Last();

        var result = await _controller.CancelExport(jobId, CancellationToken.None);

        var statusResult = result.Should().BeOfType<StatusCodeResult>().Subject;
        statusResult.StatusCode.Should().Be(202);
    }

    [Fact]
    public async Task CancelExport_UnknownJobId_Returns404()
    {
        var result = await _controller.CancelExport("nonexistent", CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(404);
    }
}
