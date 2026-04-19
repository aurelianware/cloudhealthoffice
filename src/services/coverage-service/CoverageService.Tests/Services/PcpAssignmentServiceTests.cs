using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CoverageService.Tests.Services;

/// <summary>
/// Validation-ladder coverage for PcpAssignmentService. Each test pins the
/// fail-fast contract: setting up state where multiple checks would fail and
/// asserting only the earliest one is returned.
/// </summary>
public class PcpAssignmentServiceTests
{
    private const string Tenant = "t1";
    private const string Member = "M1";
    private const string Npi = "1234567890";

    private static (PcpAssignmentService svc,
                    Mock<ICoverageRepository> coverage,
                    Mock<IPcpAssignmentRepository> assignments,
                    Mock<IProviderServiceClient> providers,
                    Mock<IPanelCounter> panel) Build()
    {
        var coverage = new Mock<ICoverageRepository>();
        var assignments = new Mock<IPcpAssignmentRepository>();
        var providers = new Mock<IProviderServiceClient>();
        var panel = new Mock<IPanelCounter>();

        // Default repository wiring: one active medical coverage on Plan-A (Commercial).
        coverage.Setup(c => c.GetActiveCoverageByMemberIdAsync(Tenant, Member, It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage>
            {
                new()
                {
                    TenantId = Tenant,
                    Id = "cov-1",
                    MemberId = Member,
                    GroupNumber = "G",
                    PlanId = "Plan-A",
                    LineOfBusiness = LineOfBusiness.Commercial,
                    EffectiveDate = DateTime.UtcNow.AddMonths(-6),
                    Status = CoverageStatus.Active
                }
            });
        coverage.Setup(c => c.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);
        assignments.Setup(a => a.AddAsync(It.IsAny<PcpAssignment>())).ReturnsAsync((PcpAssignment a) => a);
        assignments.Setup(a => a.EndOpenAssignmentsAsync(Tenant, Member, It.IsAny<DateTime>())).ReturnsAsync(0);

        var svc = new PcpAssignmentService(
            coverage.Object,
            assignments.Object,
            providers.Object,
            panel.Object,
            NullLogger<PcpAssignmentService>.Instance);

        return (svc, coverage, assignments, providers, panel);
    }

    private static AssignPcpCommand Cmd(DateTime? dob = null) => new()
    {
        ProviderNpi = Npi,
        EffectiveDate = DateTime.UtcNow.Date,
        Source = PcpAssignmentSource.MemberChoice,
        MemberDateOfBirth = dob
    };

    private static ProviderDto HappyProvider(int? panelLimit = null, int? minAge = null, int? maxAge = null)
    {
        var part = new NetworkParticipationDto
        {
            PlanId = "Plan-A",
            LineOfBusiness = LineOfBusiness.Commercial,
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            AcceptingNewPatients = true,
            PanelLimit = panelLimit,
            MinAcceptedAgeYears = minAge,
            MaxAcceptedAgeYears = maxAge
        };
        return new ProviderDto
        {
            Id = "prov-1",
            NPI = Npi,
            FullName = "Dr. Test",
            Status = ProviderStatusDto.Active,
            CredentialingStatus = CredentialingStatusDto.Approved,
            AcceptingNewPatients = true,
            NetworkParticipations = new List<NetworkParticipationDto> { part }
        };
    }

    [Fact]
    public async Task ProviderNotFound_FailsFirst()
    {
        var (svc, _, _, providers, _) = Build();
        providers.Setup(p => p.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync((ProviderDto?)null);

        var result = await svc.AssignAsync(Tenant, Member, Cmd());
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be(PcpValidationCodes.ProviderNotFound);
    }

    [Fact]
    public async Task ProviderInactive_FailsBeforeCredentialing()
    {
        var (svc, _, _, providers, _) = Build();
        var p = HappyProvider();
        p.Status = ProviderStatusDto.Terminated;
        p.CredentialingStatus = CredentialingStatusDto.Pending; // would also fail
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);

        var result = await svc.AssignAsync(Tenant, Member, Cmd());
        result.Error!.Code.Should().Be(PcpValidationCodes.ProviderInactive);
    }

    [Fact]
    public async Task NotCredentialed_FailsBeforeNetwork()
    {
        var (svc, _, _, providers, _) = Build();
        var p = HappyProvider();
        p.CredentialingStatus = CredentialingStatusDto.Pending;
        p.NetworkParticipations.Clear(); // would also fail NoNetworkParticipation
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);

        var result = await svc.AssignAsync(Tenant, Member, Cmd());
        result.Error!.Code.Should().Be(PcpValidationCodes.ProviderNotCredentialed);
    }

    [Fact]
    public async Task NoMatchingNetworkParticipation_Fails()
    {
        var (svc, _, _, providers, _) = Build();
        var p = HappyProvider();
        p.NetworkParticipations[0].LineOfBusiness = LineOfBusiness.Medicare; // member is Commercial
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);

        var result = await svc.AssignAsync(Tenant, Member, Cmd());
        result.Error!.Code.Should().Be(PcpValidationCodes.NoNetworkParticipation);
    }

    [Fact]
    public async Task PanelClosed_Fails()
    {
        var (svc, _, _, providers, _) = Build();
        var p = HappyProvider();
        p.NetworkParticipations[0].PanelAccepted = false;
        p.NetworkParticipations[0].AcceptingNewPatients = true; // panel-specific override
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);

        var result = await svc.AssignAsync(Tenant, Member, Cmd());
        result.Error!.Code.Should().Be(PcpValidationCodes.NotAcceptingPatients);
    }

    [Fact]
    public async Task LobNotAccepted_FailsBeforeAge()
    {
        var (svc, _, _, providers, _) = Build();
        var p = HappyProvider(minAge: 100); // age would also fail
        p.NetworkParticipations[0].AcceptedLobs = new List<LineOfBusiness> { LineOfBusiness.Medicaid };
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);

        var result = await svc.AssignAsync(Tenant, Member, Cmd(dob: new DateTime(1990, 1, 1)));
        result.Error!.Code.Should().Be(PcpValidationCodes.LobNotAccepted);
    }

    [Fact]
    public async Task Pediatric_RejectsAdult()
    {
        var (svc, _, _, providers, _) = Build();
        var p = HappyProvider(maxAge: 21);
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);

        var result = await svc.AssignAsync(Tenant, Member, Cmd(dob: new DateTime(1980, 1, 1)));
        result.Error!.Code.Should().Be(PcpValidationCodes.AgeOutOfRange);
    }

    [Fact]
    public async Task PanelFull_FailsLast()
    {
        var (svc, _, _, providers, panel) = Build();
        var p = HappyProvider(panelLimit: 1000);
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);
        panel.Setup(x => x.CurrentPanelCountAsync(Tenant, Npi, It.IsAny<CancellationToken>())).ReturnsAsync(1000);

        var result = await svc.AssignAsync(Tenant, Member, Cmd());
        result.Error!.Code.Should().Be(PcpValidationCodes.PanelFull);
    }

    [Fact]
    public async Task HappyPath_WritesAssignment_ClosesPrior_UpdatesCoverage()
    {
        var (svc, coverage, assignments, providers, panel) = Build();
        var p = HappyProvider(panelLimit: 100);
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(p);
        panel.Setup(x => x.CurrentPanelCountAsync(Tenant, Npi, It.IsAny<CancellationToken>())).ReturnsAsync(50);

        var result = await svc.AssignAsync(Tenant, Member, Cmd(dob: new DateTime(1980, 1, 1)));

        result.IsSuccess.Should().BeTrue();
        result.Assignment!.ProviderNpi.Should().Be(Npi);
        result.Assignment.NetworkStatusAtAssignment.Should().NotBeNullOrEmpty();
        assignments.Verify(a => a.EndOpenAssignmentsAsync(Tenant, Member, It.IsAny<DateTime>()), Times.Once);
        assignments.Verify(a => a.AddAsync(It.IsAny<PcpAssignment>()), Times.Once);
        coverage.Verify(c => c.UpdateAsync(It.IsAny<Coverage>()), Times.Once);
    }

    [Fact]
    public async Task NoActiveCoverage_FailsBeforeProviderLookup()
    {
        var (svc, coverage, _, providers, _) = Build();
        coverage.Setup(c => c.GetActiveCoverageByMemberIdAsync(Tenant, Member, It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage>());

        var result = await svc.AssignAsync(Tenant, Member, Cmd());
        result.Error!.Code.Should().Be(PcpValidationCodes.NoActiveCoverage);
        providers.Verify(p => p.GetByNpiAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvalidNpi_FailsBeforeProviderLookup()
    {
        var (svc, _, _, providers, _) = Build();

        var result = await svc.AssignAsync(Tenant, Member, new AssignPcpCommand
        {
            ProviderNpi = "abc",
            EffectiveDate = DateTime.UtcNow.Date
        });
        result.Error!.Code.Should().Be(PcpValidationCodes.InvalidNpi);
        providers.Verify(p => p.GetByNpiAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── PR #656 cleanup: denormalization scope + AdminAssigned mapping ──

    [Fact]
    public async Task HealthAndDentalCoverage_DenormStampsHealthRowOnly()
    {
        var coverageRepo = new Mock<ICoverageRepository>();
        var assignments = new Mock<IPcpAssignmentRepository>();
        var providers = new Mock<IProviderServiceClient>();
        var panel = new Mock<IPanelCounter>();

        var health = new Coverage
        {
            TenantId = Tenant,
            Id = "cov-health",
            MemberId = Member,
            GroupNumber = "G",
            PlanId = "Plan-A",
            LineOfBusiness = LineOfBusiness.Commercial,
            InsuranceLineCode = InsuranceLineCodes.Health,
            EffectiveDate = DateTime.UtcNow.AddMonths(-6),
            Status = CoverageStatus.Active
        };
        var dental = new Coverage
        {
            TenantId = Tenant,
            Id = "cov-dental",
            MemberId = Member,
            GroupNumber = "G",
            PlanId = "Plan-A",
            LineOfBusiness = LineOfBusiness.Commercial,
            InsuranceLineCode = InsuranceLineCodes.Dental,
            EffectiveDate = DateTime.UtcNow.AddMonths(-6),
            Status = CoverageStatus.Active
        };

        coverageRepo.Setup(c => c.GetActiveCoverageByMemberIdAsync(Tenant, Member, It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { health, dental });
        coverageRepo.Setup(c => c.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);
        assignments.Setup(a => a.AddAsync(It.IsAny<PcpAssignment>())).ReturnsAsync((PcpAssignment a) => a);
        assignments.Setup(a => a.EndOpenAssignmentsAsync(Tenant, Member, It.IsAny<DateTime>())).ReturnsAsync(0);
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(HappyProvider());

        var svc = new PcpAssignmentService(
            coverageRepo.Object, assignments.Object, providers.Object, panel.Object,
            NullLogger<PcpAssignmentService>.Instance);

        var result = await svc.AssignAsync(Tenant, Member, Cmd());

        result.IsSuccess.Should().BeTrue();
        health.PcpNpi.Should().Be(Npi);
        health.PcpName.Should().NotBeNullOrEmpty();
        dental.PcpNpi.Should().BeNull("dental coverage must never be stamped with a PCP");
        dental.PcpName.Should().BeNull();
        coverageRepo.Verify(c => c.UpdateAsync(It.Is<Coverage>(x => x.Id == "cov-health")), Times.Once);
        coverageRepo.Verify(c => c.UpdateAsync(It.Is<Coverage>(x => x.Id == "cov-dental")), Times.Never);
    }

    [Fact]
    public async Task NoHealthCodedCoverage_LegacyFallbackStampsActive()
    {
        // Coverage with null InsuranceLineCode (loaded before the field was
        // populated). The medical-only filter returns zero targets, so the
        // service falls back to stamping the active row — preserving legacy
        // behavior for tenants that haven't backfilled InsuranceLineCode.
        var (svc, coverage, _, providers, _) = Build();
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(HappyProvider());

        var result = await svc.AssignAsync(Tenant, Member, Cmd());

        result.IsSuccess.Should().BeTrue();
        coverage.Verify(c => c.UpdateAsync(It.IsAny<Coverage>()), Times.Once);
    }

    [Fact]
    public async Task AdminAssignedSource_MapsToAdministrativeMethod()
    {
        var coverageRepo = new Mock<ICoverageRepository>();
        var assignments = new Mock<IPcpAssignmentRepository>();
        var providers = new Mock<IProviderServiceClient>();
        var panel = new Mock<IPanelCounter>();

        var health = new Coverage
        {
            TenantId = Tenant,
            Id = "cov-health",
            MemberId = Member,
            GroupNumber = "G",
            PlanId = "Plan-A",
            LineOfBusiness = LineOfBusiness.Commercial,
            InsuranceLineCode = InsuranceLineCodes.Health,
            EffectiveDate = DateTime.UtcNow.AddMonths(-6),
            Status = CoverageStatus.Active
        };
        coverageRepo.Setup(c => c.GetActiveCoverageByMemberIdAsync(Tenant, Member, It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { health });
        coverageRepo.Setup(c => c.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);
        assignments.Setup(a => a.AddAsync(It.IsAny<PcpAssignment>())).ReturnsAsync((PcpAssignment a) => a);
        assignments.Setup(a => a.EndOpenAssignmentsAsync(Tenant, Member, It.IsAny<DateTime>())).ReturnsAsync(0);
        providers.Setup(x => x.GetByNpiAsync(Npi, It.IsAny<CancellationToken>())).ReturnsAsync(HappyProvider());

        var svc = new PcpAssignmentService(
            coverageRepo.Object, assignments.Object, providers.Object, panel.Object,
            NullLogger<PcpAssignmentService>.Instance);

        var cmd = new AssignPcpCommand
        {
            ProviderNpi = Npi,
            EffectiveDate = DateTime.UtcNow.Date,
            Source = PcpAssignmentSource.AdminAssigned
        };

        var result = await svc.AssignAsync(Tenant, Member, cmd);

        result.IsSuccess.Should().BeTrue();
        health.PcpAssignmentMethod.Should().Be(PcpAssignmentMethod.Administrative);
    }
}
