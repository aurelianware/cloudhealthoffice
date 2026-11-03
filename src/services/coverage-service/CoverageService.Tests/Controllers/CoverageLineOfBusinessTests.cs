using CloudHealthOffice.PlanConfiguration;
using CoverageService.Controllers;
using CoverageService.Models;
using CoverageService.Repositories;
using CoverageService.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CoverageService.Tests.Controllers;

/// <summary>
/// A coverage's line of business is its plan's program's
/// (docs/architecture/line-of-business.md, step 6). The create body has no line
/// of business, so before this every coverage written through the API was
/// Commercial.
/// </summary>
public class CoverageLineOfBusinessTests
{
    private const string Tenant = "t1";

    private static ProgramDescriptor Program(string id, string lineOfBusiness) => new()
    {
        Id = id, Code = id, Name = id, CarrierId = "car-1", LineOfBusiness = lineOfBusiness, StateCode = "TX",
    };

    private static (CoverageController ctl, Mock<ICoverageRepository> repo) Build(IProgramDirectory programs)
    {
        var repo = new Mock<ICoverageRepository>();
        repo.Setup(r => r.CreateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Coverage>())).ReturnsAsync((Coverage c) => c);

        var ctl = new CoverageController(
            repo.Object, new TestActor(), NullLogger<CoverageController>.Instance, programs: programs);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, repo);
    }

    private static CreateCoverageRequest Request(string planId) => new()
    {
        MemberId = "M1",
        GroupNumber = "GRP0001",
        PlanId = planId,
        EffectiveDate = DateTime.UtcNow.AddMonths(-1),
    };

    private static Coverage Created(IActionResult result) =>
        (Coverage)((CreatedAtActionResult)result).Value!;

    [Fact]
    public async Task A_new_coverage_takes_its_plans_programs_line_of_business()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("STAR", "Medicaid"), "PLAN-STAR");
        var (ctl, _) = Build(programs);

        Created(await ctl.CreateCoverage(Request("PLAN-STAR"))).LineOfBusiness.Should().Be(LineOfBusiness.Medicaid);
    }

    [Fact]
    public async Task A_legacy_program_spelling_is_read_as_the_canonical_line_of_business()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("QHP", "Marketplace"), "PLAN-QHP");
        var (ctl, _) = Build(programs);

        Created(await ctl.CreateCoverage(Request("PLAN-QHP"))).LineOfBusiness.Should().Be(LineOfBusiness.Exchange);
    }

    [Fact]
    public async Task A_plan_with_no_program_keeps_the_default()
    {
        var (ctl, _) = Build(new InMemoryProgramDirectory());

        Created(await ctl.CreateCoverage(Request("PPO500"))).LineOfBusiness.Should().Be(LineOfBusiness.Commercial);
    }

    [Fact]
    public async Task Coverage_is_still_written_when_tenant_service_does_not_answer()
    {
        var programs = new InMemoryProgramDirectory { Unavailable = true };
        programs.Add(Tenant, Program("STAR", "Medicaid"), "PLAN-STAR");
        var (ctl, repo) = Build(programs);

        Created(await ctl.CreateCoverage(Request("PLAN-STAR"))).LineOfBusiness.Should().Be(LineOfBusiness.Commercial);
        repo.Verify(r => r.CreateAsync(It.IsAny<Coverage>()), Times.Once);
    }

    [Fact]
    public async Task Moving_a_coverage_to_another_plan_takes_the_new_programs_line_of_business()
    {
        var programs = new InMemoryProgramDirectory();
        programs.Add(Tenant, Program("MA", "Medicare"), "PLAN-MA");
        var (ctl, repo) = Build(programs);
        var existing = new Coverage
        {
            Id = "cov-1", TenantId = Tenant, MemberId = "M1", GroupNumber = "GRP0001", PlanId = "PPO500",
            LineOfBusiness = LineOfBusiness.Commercial, EffectiveDate = DateTime.UtcNow.AddYears(-1),
        };
        repo.Setup(r => r.GetByIdAsync(Tenant, "cov-1")).ReturnsAsync(existing);

        var result = await ctl.UpdateCoverage("cov-1", new UpdateCoverageRequest { PlanId = "PLAN-MA" });

        ((Coverage)((OkObjectResult)result).Value!).LineOfBusiness.Should().Be(LineOfBusiness.Medicare);
    }
}
