using FhirService.Models;
using FhirService.Services;
using FluentAssertions;

namespace CloudHealthOffice.FhirService.Tests.Services;

public class BulkExportServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Patient,Coverage")]
    public async Task Initiation_RefusesRatherThanInventingCompletedFiles(string? types)
    {
        var service = new BulkExportService();
        var act = () => service.InitiateExportAsync(new BulkExportRequest { Type = types }, "tenant-a");
        await act.Should().ThrowAsync<BulkExportNotImplementedException>();
        (await service.GetJobStatusAsync("export-made-up", "tenant-a")).Should().BeNull();
        (await service.CancelJobAsync("export-made-up", "tenant-a")).Should().BeFalse();
    }

    [Fact]
    public async Task CancellationIsPreserved()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => new BulkExportService().InitiateExportAsync(new BulkExportRequest(), "tenant-a", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
