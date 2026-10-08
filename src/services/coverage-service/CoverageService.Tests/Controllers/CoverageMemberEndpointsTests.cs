using CoverageService.Controllers;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using CoverageService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CoverageService.Tests.Controllers;

public class CoverageMemberEndpointsTests
{
    private const string Tenant = "t1";

    private static (CoverageController ctl, Mock<ICoverageRepository> repo, Mock<IPcpAssignmentService> pcp) Build()
    {
        var repo = new Mock<ICoverageRepository>();
        var pcp = new Mock<IPcpAssignmentService>();
        var careTeam = new Mock<ICareTeamProjector>();
        var ctl = new CoverageController(
            repo.Object,
            new TestActor(),
            NullLogger<CoverageController>.Instance,
            pcp.Object,
            careTeam.Object);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, repo, pcp);
    }

    private static Coverage ActiveCoverage(string memberId, string? pcpNpi = null) => new()
    {
        TenantId = Tenant,
        MemberId = memberId,
        GroupNumber = "G",
        PlanId = "P",
        EffectiveDate = DateTime.UtcNow.AddMonths(-6),
        Status = CoverageStatus.Active,
        PcpNpi = pcpNpi,
        PcpAssignmentDate = pcpNpi != null ? DateTime.UtcNow.AddMonths(-3) : null
    };

    [Fact]
    public async Task GetMemberPcp_WithAssignment_ReturnsPcp()
    {
        var (ctl, repo, _) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { ActiveCoverage("M1", "1234567890") });

        var resp = await ctl.GetMemberPcp("M1");
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<MemberPcpResponse>().Subject;
        body.NPI.Should().Be("1234567890");
    }

    [Fact]
    public async Task GetMemberPcp_NoActiveCoverage_Returns404()
    {
        var (ctl, repo, _) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage>());

        (await ctl.GetMemberPcp("M1")).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetMemberPcp_ActiveCoverageWithoutPcp_Returns404()
    {
        var (ctl, repo, _) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { ActiveCoverage("M1") });

        (await ctl.GetMemberPcp("M1")).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task AssignMemberPcp_DelegatesToServiceAndReturnsOk()
    {
        var (ctl, _, pcp) = Build();
        pcp.Setup(p => p.AssignAsync(Tenant, "M1", It.IsAny<AssignPcpCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PcpAssignmentResult.Ok(new PcpAssignment
            {
                TenantId = Tenant,
                MemberId = "M1",
                CoverageId = "cov1",
                ProviderNpi = "1234567890",
                ProviderName = "Dr. Test",
                EffectiveDate = DateTime.UtcNow.Date,
                NetworkStatusAtAssignment = "InNetwork"
            }));

        var resp = await ctl.AssignMemberPcp("M1",
            new AssignPcpBody { ProviderNpi = "1234567890", EffectiveDate = DateTime.UtcNow.Date },
            CancellationToken.None);

        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<MemberPcpResponse>().Subject;
        body.NPI.Should().Be("1234567890");
    }

    [Fact]
    public async Task AssignMemberPcp_ValidationFailure_ReturnsBadRequestWithError()
    {
        var (ctl, _, pcp) = Build();
        pcp.Setup(p => p.AssignAsync(Tenant, "M1", It.IsAny<AssignPcpCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PcpAssignmentResult.Fail(new PcpValidationError(
                PcpValidationCodes.PanelFull, "providerNpi", "Provider panel is full (1000 / 1000).")));

        var resp = await ctl.AssignMemberPcp("M1",
            new AssignPcpBody { ProviderNpi = "1234567890", EffectiveDate = DateTime.UtcNow.Date },
            CancellationToken.None);

        var bad = resp.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = bad.Value.Should().BeOfType<PcpValidationError>().Subject;
        body.Code.Should().Be(PcpValidationCodes.PanelFull);
    }

    [Fact]
    public async Task AssignMemberPcp_NoActiveCoverage_Returns404()
    {
        var (ctl, _, pcp) = Build();
        pcp.Setup(p => p.AssignAsync(Tenant, "M1", It.IsAny<AssignPcpCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PcpAssignmentResult.Fail(new PcpValidationError(
                PcpValidationCodes.NoActiveCoverage, "memberId", "No active coverage.")));

        var resp = await ctl.AssignMemberPcp("M1",
            new AssignPcpBody { ProviderNpi = "1234567890" },
            CancellationToken.None);

        resp.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task TerminateMemberCoverage_ActiveCoverages_MarksTerminated()
    {
        var (ctl, repo, _) = Build();
        var c1 = ActiveCoverage("M1");
        var c2 = ActiveCoverage("M1");
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { c1, c2 });
        repo.Setup(r => r.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);

        var resp = await ctl.TerminateMemberCoverage("M1",
            new TerminateMemberCoverageBody { TerminationDate = DateTime.UtcNow.Date, ReasonCode = "25" });

        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<TerminateMemberCoverageResponse>().Subject;
        body.TerminatedCount.Should().Be(2);
        c1.Status.Should().Be(CoverageStatus.Terminated);
        c2.Status.Should().Be(CoverageStatus.Terminated);
        repo.Verify(r => r.UpdateAsync(It.IsAny<Coverage>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TerminateMemberCoverage_NoActiveCoverage_Returns404()
    {
        var (ctl, repo, _) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage>());

        var resp = await ctl.TerminateMemberCoverage("M1",
            new TerminateMemberCoverageBody { TerminationDate = DateTime.UtcNow.Date });
        resp.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task TerminateMemberCoverage_SkipsCoverageAlreadyTerminated()
    {
        // The DOS query returns coverages terminated on/after asOf because they
        // were in force that day; the endpoint must not re-terminate them.
        var (ctl, repo, _) = Build();
        var open = ActiveCoverage("M1");
        var alreadyTermed = ActiveCoverage("M1");
        alreadyTermed.Status = CoverageStatus.Terminated;
        alreadyTermed.TerminationDate = DateTime.UtcNow.Date.AddDays(10);
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { open, alreadyTermed });
        repo.Setup(r => r.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);

        var resp = await ctl.TerminateMemberCoverage("M1",
            new TerminateMemberCoverageBody { TerminationDate = DateTime.UtcNow.Date });

        var body = resp.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<TerminateMemberCoverageResponse>().Subject;
        body.TerminatedCount.Should().Be(1);
        alreadyTermed.TerminationDate.Should().Be(DateTime.UtcNow.Date.AddDays(10));
        repo.Verify(r => r.UpdateAsync(alreadyTermed), Times.Never);
    }

    [Fact]
    public async Task TerminateMemberCoverage_OnlyAlreadyTerminated_Returns404()
    {
        var (ctl, repo, _) = Build();
        var alreadyTermed = ActiveCoverage("M1");
        alreadyTermed.Status = CoverageStatus.Terminated;
        alreadyTermed.TerminationDate = DateTime.UtcNow.Date;
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { alreadyTermed });

        var resp = await ctl.TerminateMemberCoverage("M1",
            new TerminateMemberCoverageBody { TerminationDate = DateTime.UtcNow.Date });
        resp.Should().BeOfType<NotFoundObjectResult>();
        repo.Verify(r => r.UpdateAsync(It.IsAny<Coverage>()), Times.Never);
    }

    [Fact]
    public async Task UpdateCoverage_SetTerminatedWithoutTerminationDate_Returns400()
    {
        var (ctl, repo, _) = Build();
        var coverage = ActiveCoverage("M1");
        repo.Setup(r => r.GetByIdAsync(Tenant, "cov-1")).ReturnsAsync(coverage);

        var resp = await ctl.UpdateCoverage("cov-1",
            new UpdateCoverageRequest { Status = CoverageStatus.Terminated });

        resp.Should().BeOfType<BadRequestObjectResult>();
        coverage.Status.Should().Be(CoverageStatus.Active);
        repo.Verify(r => r.UpdateAsync(It.IsAny<Coverage>()), Times.Never);
    }

    [Fact]
    public async Task UpdateCoverage_SetTerminatedWithExistingTerminationDate_Succeeds()
    {
        var (ctl, repo, _) = Build();
        var coverage = ActiveCoverage("M1");
        coverage.TerminationDate = DateTime.UtcNow.Date.AddDays(30);
        repo.Setup(r => r.GetByIdAsync(Tenant, "cov-1")).ReturnsAsync(coverage);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);

        var resp = await ctl.UpdateCoverage("cov-1",
            new UpdateCoverageRequest { Status = CoverageStatus.Terminated });

        resp.Should().BeOfType<OkObjectResult>();
        coverage.Status.Should().Be(CoverageStatus.Terminated);
    }
}
