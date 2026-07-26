using EnrollmentImportService.Controllers;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using EnrollmentImportService.Services.Edi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EnrollmentImportService.Tests.Controllers;

/// <summary>Covers the run-history read path added alongside <see cref="EnrollmentImportRun"/>.</summary>
public class EnrollmentControllerTests
{
    private static (EnrollmentController ctl, Mock<IEnrollmentImportRunRepository> runs) Build()
    {
        var importService = new Mock<IEnrollmentImportService>();
        var ediParser = new Mock<IEnrollment834EdiParser>();
        var gapReportService = new Mock<IPlanCodeGapReportService>();
        var runs = new Mock<IEnrollmentImportRunRepository>();

        var ctl = new EnrollmentController(
            importService.Object, ediParser.Object, gapReportService.Object, runs.Object,
            NullLogger<EnrollmentController>.Instance);

        return (ctl, runs);
    }

    [Fact]
    public async Task ListImportRuns_ReturnsRepoResult()
    {
        var (ctl, runs) = Build();
        runs.Setup(r => r.ListRecentAsync("t1", 100))
            .ReturnsAsync(new List<EnrollmentImportRun>
            {
                new() { TenantId = "t1", BatchId = "B1", SuccessCount = 2 },
                new() { TenantId = "t1", BatchId = "B2", SuccessCount = 5 }
            });

        var resp = await ctl.ListImportRuns("t1", 100);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var list = (IReadOnlyList<EnrollmentImportRun>)ok.Value!;
        list.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListImportRuns_MissingTenant_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        (await ctl.ListImportRuns("")).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ListImportRuns_LimitOutOfRange_ClampsTo100()
    {
        var (ctl, runs) = Build();
        runs.Setup(r => r.ListRecentAsync("t1", 100))
            .ReturnsAsync(new List<EnrollmentImportRun>());

        await ctl.ListImportRuns("t1", 99999);
        runs.Verify(r => r.ListRecentAsync("t1", 100), Times.Once);

        await ctl.ListImportRuns("t1", 0);
        runs.Verify(r => r.ListRecentAsync("t1", 100), Times.Exactly(2));
    }
}
