using EnrollmentImportService.Clients;
using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Moq;

namespace EnrollmentImportService.Tests.Services;

public class PlanCodeGapReportServiceTests
{
    private static MemberEnrollment Subscriber(string groupNumber, params CoverageDetail[] coverage) => new()
    {
        SubscriberId = "SUB-1",
        MaintenanceType = "021",
        BenefitStatus = "A",
        Relationship = "18",
        GroupNumber = groupNumber,
        Coverage = coverage.ToList()
    };

    [Fact]
    public async Task BuildReportAsync_MappedCode_GoesToMapped()
    {
        var client = new Mock<IBenefitPlanServiceClient>();
        client.Setup(c => c.ResolvePlanIdAsync("t1", "GRP0001", "HLT", "PPO2026", It.IsAny<CancellationToken>()))
            .ReturnsAsync("plan-guid-123");
        var svc = new PlanCodeGapReportService(client.Object);

        var batch = new Enrollment834
        {
            FileName = "sample.834",
            Enrollments = new()
            {
                Subscriber("GRP0001", new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" })
            }
        };

        var report = await svc.BuildReportAsync(batch, "t1");

        report.Mapped.Should().ContainSingle();
        report.Mapped[0].PlanId.Should().Be("plan-guid-123");
        report.Unmapped.Should().BeEmpty();
        report.IncompleteCount.Should().Be(0);
    }

    [Fact]
    public async Task BuildReportAsync_UnmappedCode_GoesToUnmapped()
    {
        var client = new Mock<IBenefitPlanServiceClient>();
        client.Setup(c => c.ResolvePlanIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var svc = new PlanCodeGapReportService(client.Object);

        var batch = new Enrollment834
        {
            FileName = "sample.834",
            Enrollments = new()
            {
                Subscriber("GRP0001", new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "UNKNOWN" })
            }
        };

        var report = await svc.BuildReportAsync(batch, "t1");

        report.Unmapped.Should().ContainSingle(e => e.ExternalPlanCode == "UNKNOWN");
        report.Mapped.Should().BeEmpty();
    }

    [Fact]
    public async Task BuildReportAsync_MissingGroupOrCode_CountsAsIncomplete_NotUnmapped()
    {
        var client = new Mock<IBenefitPlanServiceClient>();
        var svc = new PlanCodeGapReportService(client.Object);

        var batch = new Enrollment834
        {
            FileName = "sample.834",
            Enrollments = new()
            {
                Subscriber(null!, new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" }),
                Subscriber("GRP0002", new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = null })
            }
        };

        var report = await svc.BuildReportAsync(batch, "t1");

        report.IncompleteCount.Should().Be(2);
        report.Mapped.Should().BeEmpty();
        report.Unmapped.Should().BeEmpty();
        client.Verify(c => c.ResolvePlanIdAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BuildReportAsync_DuplicateTriplesAcrossSubscribers_AreCheckedOnce()
    {
        var client = new Mock<IBenefitPlanServiceClient>();
        client.Setup(c => c.ResolvePlanIdAsync("t1", "GRP0001", "HLT", "PPO2026", It.IsAny<CancellationToken>()))
            .ReturnsAsync("plan-guid-123");
        var svc = new PlanCodeGapReportService(client.Object);

        var batch = new Enrollment834
        {
            FileName = "sample.834",
            Enrollments = new()
            {
                Subscriber("GRP0001", new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" }),
                Subscriber("GRP0001", new CoverageDetail { InsuranceLineCode = "HLT", PlanCoverageDescription = "PPO2026" })
            }
        };

        var report = await svc.BuildReportAsync(batch, "t1");

        report.Mapped.Should().ContainSingle();
        client.Verify(c => c.ResolvePlanIdAsync(
            "t1", "GRP0001", "HLT", "PPO2026", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BuildReportAsync_DependentCoverage_UsesSubscribersGroupNumber()
    {
        var client = new Mock<IBenefitPlanServiceClient>();
        client.Setup(c => c.ResolvePlanIdAsync("t1", "GRP0001", "DEN", "DENTAL-KIDS", It.IsAny<CancellationToken>()))
            .ReturnsAsync("dental-plan-guid");
        var svc = new PlanCodeGapReportService(client.Object);

        var subscriber = Subscriber("GRP0001");
        subscriber.Dependents.Add(new Dependent
        {
            Coverage = new() { new CoverageDetail { InsuranceLineCode = "DEN", PlanCoverageDescription = "DENTAL-KIDS" } }
        });

        var batch = new Enrollment834
        {
            FileName = "sample.834",
            Enrollments = new() { subscriber }
        };

        var report = await svc.BuildReportAsync(batch, "t1");

        report.Mapped.Should().ContainSingle(e => e.GroupNumber == "GRP0001" && e.ExternalPlanCode == "DENTAL-KIDS");
    }
}
