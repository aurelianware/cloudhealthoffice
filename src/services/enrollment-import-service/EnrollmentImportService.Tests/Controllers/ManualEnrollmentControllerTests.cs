using EnrollmentImportService.Controllers;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace EnrollmentImportService.Tests.Controllers;

public class ManualEnrollmentControllerTests
{
    private static (ManualEnrollmentController ctl, Mock<IEnrollmentImportService> svc) Build()
    {
        var svc = new Mock<IEnrollmentImportService>();
        svc.Setup(s => s.ImportEnrollmentAsync(It.IsAny<Enrollment834>(), It.IsAny<string>()))
            .ReturnsAsync(new ImportResult { BatchId = "B-1", SuccessCount = 1 });
        var ctl = new ManualEnrollmentController(
            svc.Object,
            new EnrollmentValidator(),
            NullLogger<ManualEnrollmentController>.Instance);
        return (ctl, svc);
    }

    private static MemberEnrollment Valid() => new()
    {
        SubscriberId = "M-001",
        MaintenanceType = "021",
        BenefitStatus = "A",
        Relationship = "18",
        EnrollmentDate = "2026-01-01",
        Demographics = new Demographics { FirstName = "Jane", LastName = "Doe" }
    };

    [Fact]
    public async Task CreateManual_ValidPayload_DefaultsEventId_AndImports()
    {
        var (ctl, svc) = Build();
        var resp = await ctl.CreateManual(Valid(), "t1", "actor1");

        resp.Should().BeOfType<OkObjectResult>();
        svc.Verify(s => s.ImportEnrollmentAsync(
            It.Is<Enrollment834>(b =>
                b.ManualSource &&
                b.Enrollments.Count == 1 &&
                !string.IsNullOrEmpty(b.Enrollments[0].EventId)),
            "t1"), Times.Once);
    }

    [Fact]
    public async Task CreateManual_PreservesSuppliedEventId_ForRetrySafety()
    {
        var (ctl, svc) = Build();
        var enrollment = Valid();
        enrollment.EventId = "client-supplied-key";

        await ctl.CreateManual(enrollment, "t1");

        svc.Verify(s => s.ImportEnrollmentAsync(
            It.Is<Enrollment834>(b => b.Enrollments[0].EventId == "client-supplied-key"),
            "t1"), Times.Once);
    }

    [Fact]
    public async Task CreateManual_MissingTenant_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        var resp = await ctl.CreateManual(Valid(), tenantId: "");
        resp.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateManual_InvalidPayload_ReturnsValidationProblemWithFieldKeys()
    {
        var (ctl, _) = Build();
        var resp = await ctl.CreateManual(new MemberEnrollment(), "t1");
        var bad = resp.Should().BeOfType<BadRequestObjectResult>().Subject;
        var problem = bad.Value.Should().BeOfType<ValidationProblemDetails>().Subject;
        problem.Errors.Keys.Should().Contain("subscriberId");
        problem.Errors.Keys.Should().Contain("maintenanceType");
    }
}
