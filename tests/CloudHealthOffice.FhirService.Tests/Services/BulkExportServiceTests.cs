using FhirService.Models;
using FhirService.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class BulkExportServiceTests
{
    private readonly BulkExportService _service;

    public BulkExportServiceTests()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["Fhir:ServerBaseUrl"]).Returns("https://test.example.com/fhir/r4");
        var logger = new Mock<ILogger<BulkExportService>>();
        _service = new BulkExportService(config.Object, logger.Object);
    }

    [Fact]
    public async Task InitiateExport_CreatesJobWithCorrectResourceTypes()
    {
        var request = new BulkExportRequest { Type = "Patient,Coverage" };
        var job = await _service.InitiateExportAsync(request, "test-tenant");

        job.JobId.Should().StartWith("export-");
        job.ResourceTypes.Should().BeEquivalentTo(new[] { "Patient", "Coverage" });
        job.Status.Should().Be(BulkExportStatus.Complete);
        job.Manifest.Should().NotBeNull();
        job.Manifest!.Output.Should().HaveCount(2);
    }

    [Fact]
    public async Task InitiateExport_DefaultResourceTypes_IncludesAll()
    {
        var request = new BulkExportRequest();
        var job = await _service.InitiateExportAsync(request, "test-tenant");

        job.ResourceTypes.Should().Contain("Patient");
        job.ResourceTypes.Should().Contain("ExplanationOfBenefit");
        job.ResourceTypes.Should().Contain("Coverage");
        job.ResourceTypes.Should().Contain("Encounter");
    }

    [Fact]
    public async Task GetJobStatus_ExistingJob_ReturnsJob()
    {
        var request = new BulkExportRequest { Type = "Patient" };
        var created = await _service.InitiateExportAsync(request, "test-tenant");

        var retrieved = await _service.GetJobStatusAsync(created.JobId, "test-tenant");

        retrieved.Should().NotBeNull();
        retrieved!.JobId.Should().Be(created.JobId);
    }

    [Fact]
    public async Task GetJobStatus_WrongTenant_ReturnsNull()
    {
        var request = new BulkExportRequest { Type = "Patient" };
        var created = await _service.InitiateExportAsync(request, "tenant-a");

        var retrieved = await _service.GetJobStatusAsync(created.JobId, "tenant-b");

        retrieved.Should().BeNull();
    }

    [Fact]
    public async Task CancelJob_ExistingJob_SetsStatusCancelled()
    {
        var request = new BulkExportRequest { Type = "Patient" };
        var created = await _service.InitiateExportAsync(request, "test-tenant");

        var cancelled = await _service.CancelJobAsync(created.JobId, "test-tenant");

        cancelled.Should().BeTrue();

        var job = await _service.GetJobStatusAsync(created.JobId, "test-tenant");
        job!.Status.Should().Be(BulkExportStatus.Cancelled);
    }
}
