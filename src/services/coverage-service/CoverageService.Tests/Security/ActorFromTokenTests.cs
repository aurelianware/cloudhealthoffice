using CoverageService.Controllers;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Services;
using CoverageService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CoverageService.Tests.Security;

/// <summary>
/// Every coverage write records the token subject (ICurrentActor.UserId), not the
/// display name and never a "System" / "member-service" placeholder.
/// </summary>
public class ActorFromTokenTests
{
    private const string Tenant = "t1";
    private const string UserId = "enroll-user-7";

    private readonly Mock<ICoverageRepository> _repo = new();
    private readonly Mock<IPcpAssignmentService> _pcp = new();

    private CoverageController Build()
    {
        _repo.Setup(r => r.CreateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);
        var ctl = new CoverageController(_repo.Object, new TestActor(UserId, Tenant),
            NullLogger<CoverageController>.Instance, _pcp.Object, Mock.Of<ICareTeamProjector>());
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctl;
    }

    private static Coverage Existing() => new()
    {
        Id = "cov-1", TenantId = Tenant, MemberId = "M1", GroupNumber = "G", PlanId = "P",
        EffectiveDate = DateTime.UtcNow.AddMonths(-6), Status = CoverageStatus.Active,
        CreatedBy = "original-creator"
    };

    [Fact]
    public async Task CreateCoverage_RecordsCreatorFromToken()
    {
        var ctl = Build();

        var result = await ctl.CreateCoverage(new CreateCoverageRequest
        {
            MemberId = "M1", GroupNumber = "G", PlanId = "P", EffectiveDate = DateTime.UtcNow.Date
        });

        var created = (Coverage)((CreatedAtActionResult)result).Value!;
        created.CreatedBy.Should().Be(UserId);
        created.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task UpdateCoverage_RecordsUpdaterFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetByIdAsync(Tenant, "cov-1")).ReturnsAsync(Existing());

        var result = await ctl.UpdateCoverage("cov-1", new UpdateCoverageRequest { PlanId = "P2" });

        var updated = (Coverage)((OkObjectResult)result).Value!;
        updated.LastUpdatedBy.Should().Be(UserId);
        updated.CreatedBy.Should().Be("original-creator");
    }

    [Fact]
    public async Task TerminateCoverage_RecordsUpdaterFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetByIdAsync(Tenant, "cov-1")).ReturnsAsync(Existing());

        await ctl.TerminateCoverage("cov-1");

        _repo.Verify(r => r.UpdateAsync(It.Is<Coverage>(c =>
            c.Status == CoverageStatus.Terminated && c.LastUpdatedBy == UserId)));
    }

    [Fact]
    public async Task TerminateMemberCoverage_RecordsUpdaterFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetActiveCoverageByMemberIdAsync(Tenant, "M1", It.IsAny<DateTime>(), null))
            .ReturnsAsync(new List<Coverage> { Existing() });

        await ctl.TerminateMemberCoverage("M1", new TerminateMemberCoverageBody { TerminationDate = DateTime.UtcNow.Date });

        _repo.Verify(r => r.UpdateAsync(It.Is<Coverage>(c => c.LastUpdatedBy == UserId)), Times.Once);
    }

    [Fact]
    public async Task AssignMemberPcp_RecordsAssignerFromToken()
    {
        var ctl = Build();
        AssignPcpCommand? sent = null;
        _pcp.Setup(p => p.AssignAsync(Tenant, "M1", It.IsAny<AssignPcpCommand>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, AssignPcpCommand, CancellationToken>((_, _, cmd, _) => sent = cmd)
            .ReturnsAsync(PcpAssignmentResult.Fail(new PcpValidationError(
                PcpValidationCodes.PanelFull, "providerNpi", "full")));

        await ctl.AssignMemberPcp("M1",
            new AssignPcpBody { ProviderNpi = "1234567890", EffectiveDate = DateTime.UtcNow.Date },
            CancellationToken.None);

        sent.Should().NotBeNull();
        sent!.AssignedBy.Should().Be(UserId);
    }
}
