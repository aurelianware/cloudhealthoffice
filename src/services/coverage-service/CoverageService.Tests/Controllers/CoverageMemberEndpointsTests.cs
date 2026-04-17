using CoverageService.Controllers;
using CoverageService.Models;
using CoverageService.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CoverageService.Tests.Controllers;

public class CoverageMemberEndpointsTests
{
    private const string Tenant = "t1";

    private static (CoverageController ctl, Mock<ICoverageRepository> repo) Build()
    {
        var repo = new Mock<ICoverageRepository>();
        var ctl = new CoverageController(repo.Object, NullLogger<CoverageController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, repo);
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
        var (ctl, repo) = Build();
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
        var (ctl, repo) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage>());

        (await ctl.GetMemberPcp("M1")).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetMemberPcp_ActiveCoverageWithoutPcp_Returns404()
    {
        var (ctl, repo) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { ActiveCoverage("M1") });

        (await ctl.GetMemberPcp("M1")).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task AssignMemberPcp_ValidNpi_WritesAndMovesPrior()
    {
        var (ctl, repo) = Build();
        var existing = ActiveCoverage("M1", "9999999999");
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { existing });
        repo.Setup(r => r.UpdateAsync(It.IsAny<Coverage>()))
            .ReturnsAsync((Coverage c) => c);

        var resp = await ctl.AssignMemberPcp("M1",
            new AssignPcpBody { ProviderNpi = "1234567890", EffectiveDate = DateTime.UtcNow.Date });

        resp.Should().BeOfType<OkObjectResult>();
        existing.PcpNpi.Should().Be("1234567890");
        existing.PreviousPcpNpi.Should().Be("9999999999");
        repo.Verify(r => r.UpdateAsync(It.IsAny<Coverage>()), Times.Once);
    }

    [Fact]
    public async Task AssignMemberPcp_InvalidNpi_ReturnsBadRequest()
    {
        var (ctl, _) = Build();
        var resp = await ctl.AssignMemberPcp("M1", new AssignPcpBody { ProviderNpi = "bad" });
        resp.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task AssignMemberPcp_NoActiveCoverage_Returns404()
    {
        var (ctl, repo) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage>());

        var resp = await ctl.AssignMemberPcp("M1",
            new AssignPcpBody { ProviderNpi = "1234567890" });
        resp.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task TerminateMemberCoverage_ActiveCoverages_MarksTerminated()
    {
        var (ctl, repo) = Build();
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
        var (ctl, repo) = Build();
        repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage>());

        var resp = await ctl.TerminateMemberCoverage("M1",
            new TerminateMemberCoverageBody { TerminationDate = DateTime.UtcNow.Date });
        resp.Should().BeOfType<NotFoundObjectResult>();
    }
}
