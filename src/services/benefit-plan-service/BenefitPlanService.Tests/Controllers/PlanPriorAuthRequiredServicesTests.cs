using BenefitPlanService.Controllers;
using BenefitPlanService.Models;
using BenefitPlanService.Services;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Controllers;

/// <summary>A plan's PA-required list, keyed by its program (GET api/v1/prior-auth-rules/plans/{planId}/required-services).</summary>
public class PlanPriorAuthRequiredServicesTests
{
    private readonly Mock<IPaRuleRepository> _repo = new();
    private readonly Mock<ICurrentActor> _actor = new();
    private readonly Mock<IBenefitPlanService> _plans = new();
    private readonly Mock<IProgramDirectory> _programs = new();

    public PlanPriorAuthRequiredServicesTests()
    {
        _actor.SetupGet(a => a.TenantId).Returns("tenant-a");
        _repo.Setup(r => r.GetRulesAsync(It.IsAny<RuleSetKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PaRuleDocument>());
        _repo.Setup(r => r.GetRulesAsync(It.Is<RuleSetKey>(k => k.TenantId == "tenant-a" && k.StateCode == "TX" && k.Program == "STAR"),
            It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new PaRuleDocument {
                RuleId = "import:imaging", RuleName = "Imaging", StateCode = "TX", Lob = PaLineOfBusiness.Medicaid, Program = "STAR",
                TenantId = "tenant-a", Category = RuleCategory.ClinicalCriteria, Scope = RuleScope.Tenant, Priority = 10,
                RuleType = "ProcedureRequiresAuth", ProcedureCodes = ["70551", "J9035"] } });
    }

    private PriorAuthRulesController Controller() =>
        new(_repo.Object, _actor.Object, NullLogger<PriorAuthRulesController>.Instance, _plans.Object, _programs.Object);

    private void Plan(string? programId) =>
        _plans.Setup(p => p.GetPlanAsync("plan-1", "tenant-a")).ReturnsAsync(new BenefitPlan { Id = "plan-1", ProgramId = programId });

    private void Program(string lob = "Medicaid") =>
        _programs.Setup(p => p.GetAsync("tenant-a", "prog-1", It.IsAny<CancellationToken>())).ReturnsAsync(new ProgramDescriptor
        {
            Id = "prog-1", Code = "STAR", Name = "STAR", CarrierId = "c", LineOfBusiness = lob, StateCode = "TX",
        });

    [Theory]
    [InlineData(false, new[] { "70551" })]
    [InlineData(true, new[] { "70551", "J9035" })]
    public async Task Resolves_the_plans_program_rule_set(bool includeDrugs, string[] codes)
    {
        Plan("prog-1");
        Program();

        var ok = (await Controller().PlanRequiredServices("plan-1", includeDrugs, default)).Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<PlanPaRequiredServices>().Subject;

        body.Resolved.Should().BeTrue();
        body.List!.RuleSet.Should().Be("prog-1");
        body.List.Entries.Single().Codes.Should().Equal(codes);
    }

    [Fact]
    public async Task A_plan_without_a_program_is_unresolved_not_an_error()
    {
        Plan(null);
        var body = ((OkObjectResult)await Controller().PlanRequiredServices("plan-1", false, default)).Value as PlanPaRequiredServices;
        body!.Resolved.Should().BeFalse();
        body.Reason.Should().Contain("no program");
    }

    [Fact]
    public async Task Unknown_plan_is_404_and_a_program_outage_is_503()
    {
        (await Controller().PlanRequiredServices("missing", false, default)).Should().BeOfType<NotFoundObjectResult>();

        Plan("prog-1");
        _programs.Setup(p => p.GetAsync("tenant-a", "prog-1", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("down"));
        (await Controller().PlanRequiredServices("plan-1", false, default)).Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(503);
    }
}
