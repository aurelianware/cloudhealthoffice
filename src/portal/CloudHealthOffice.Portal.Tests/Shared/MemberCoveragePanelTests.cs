using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared;

/// <summary>
/// bUnit tests for <see cref="MemberCoveragePanel"/>, the member record's
/// Coverage &amp; Benefits view. Each backend call is independent: one
/// failing shows "unavailable" for that piece only.
/// </summary>
public class MemberCoveragePanelTests : TestContext
{
    private readonly Mock<ICoverageService> _coverage = new();
    private readonly Mock<IMemberService> _members = new();
    private readonly Mock<IBenefitPlanService> _plans = new();

    public MemberCoveragePanelTests()
    {
        _coverage.Setup(c => c.GetCoverageByMemberIdAsync("MBR-1")).ReturnsAsync(new List<Coverage>
        {
            new()
            {
                CoverageId = "COV-1", MemberId = "MBR-1", PlanId = "PLAN-GOLD", PlanName = "Gold PPO",
                GroupNumber = "GRP-100", CoverageLevel = "FAM", Status = 1, LineOfBusiness = 1,
                EffectiveDate = DateTime.Today.AddYears(-1), TerminationDate = null
            },
            new()
            {
                CoverageId = "COV-0", MemberId = "MBR-1", PlanId = "PLAN-OLD", PlanName = "Old Bronze",
                Status = 3, LineOfBusiness = 1,
                EffectiveDate = DateTime.Today.AddYears(-3), TerminationDate = DateTime.Today.AddYears(-1).AddDays(-1)
            }
        });
        _plans.Setup(p => p.GetMemberViewAsync("PLAN-GOLD", It.IsAny<DateTime>())).ReturnsAsync(new MemberBenefitView
        {
            PlanId = "PLAN-GOLD", PlanName = "Gold PPO", PlanType = "PPO", Payer = "CHO Health",
            CostSharing = new MemberBenefitCostSharing { IndividualDeductible = 1500, FamilyDeductible = 3000 }
        });
        _members.Setup(m => m.GetMemberPcpAsync("MBR-1")).ReturnsAsync(new MemberPcp
        {
            ProviderName = "Dr. Rivera", NPI = "1234567890", NetworkStatus = "In-Network"
        });
        _members.Setup(m => m.GetAccumulatorsAsync("MBR-1")).ReturnsAsync(new MemberAccumulators
        {
            MemberId = "MBR-1",
            PlanYearStart = new DateTime(DateTime.Today.Year, 1, 1),
            PlanYearEnd = new DateTime(DateTime.Today.Year, 12, 31),
            IndividualDeductibleUsed = 400, IndividualDeductibleLimit = 1500,
            FamilyDeductibleUsed = 900, FamilyDeductibleLimit = 3000,
            IndividualOopUsed = 600, IndividualOopLimit = 6000,
            FamilyOopUsed = 1200, FamilyOopLimit = 12000
        });

        Services.AddSingleton(_coverage.Object);
        Services.AddSingleton(_members.Object);
        Services.AddSingleton(_plans.Object);
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<MemberCoveragePanel> RenderPanel(DateTime? asOf = null) =>
        RenderComponent<MemberCoveragePanel>(p => p
            .Add(x => x.MemberId, "MBR-1")
            .Add(x => x.AsOf, asOf));

    [Fact]
    public void AllCallsSucceed_ShowsActiveCoveragePlanNetworkPcpAndAccumulators()
    {
        var cut = RenderPanel();

        cut.WaitForAssertion(() => cut.Find("[data-testid='accumulators']"));
        cut.Find("[data-testid='coverage-status']").TextContent.Should().Contain("ACTIVE COVERAGE");
        var rows = cut.FindAll("[data-testid='coverage-row']").ToList();
        rows.Should().ContainSingle("only the coverage in force today is shown");
        rows.Single().TextContent.Should().Contain("Gold PPO").And.Contain("PLAN-GOLD").And.Contain("Commercial").And.Contain("GRP-100");
        cut.Markup.Should().Contain("PPO").And.Contain("Dr. Rivera");
        var accum = cut.Find("[data-testid='accumulators']").TextContent;
        // Currency text follows the ambient culture (the portal sets en-US at startup).
        accum.Should().Contain($"{400m:C} of {1500m:C}").And.Contain($"{1200m:C} of {12000m:C}");
    }

    [Fact]
    public void AccumulatorCallFails_ShowsUnavailableForAccumulatorsOnly()
    {
        _members.Setup(m => m.GetAccumulatorsAsync("MBR-1"))
            .ThrowsAsync(new ServiceUnavailableException("Member Service", new HttpRequestException("boom")));

        var cut = RenderPanel();

        cut.WaitForAssertion(() => cut.Find("[data-testid='accumulators-unavailable']"));
        cut.FindAll("[data-testid='accumulators']").Should().BeEmpty();
        cut.Find("[data-testid='coverage-status']").TextContent.Should().Contain("ACTIVE COVERAGE");
        cut.Markup.Should().Contain("Gold PPO").And.Contain("Dr. Rivera");
        cut.FindAll("[data-testid='coverage-unavailable']").Should().BeEmpty();
        cut.FindAll("[data-testid='pcp-unavailable']").Should().BeEmpty();
    }

    [Fact]
    public void CoverageCallFails_PcpAndAccumulatorsStillRender()
    {
        _coverage.Setup(c => c.GetCoverageByMemberIdAsync("MBR-1"))
            .ThrowsAsync(new ServiceUnavailableException("Coverage Service", new HttpRequestException("down")));

        var cut = RenderPanel();

        cut.WaitForAssertion(() => cut.Find("[data-testid='coverage-unavailable']"));
        cut.WaitForAssertion(() => cut.Find("[data-testid='accumulators']"));
        cut.Find("[data-testid='pcp-name']").TextContent.Should().Contain("Dr. Rivera");
    }

    [Fact]
    public void PcpNotFound_ShowsNoPcpAssigned_NotUnavailable()
    {
        _members.Setup(m => m.GetMemberPcpAsync("MBR-1")).ThrowsAsync(new ServiceUnavailableException(
            "Member Service", new HttpRequestException("nf", null, System.Net.HttpStatusCode.NotFound)));

        var cut = RenderPanel();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No PCP assigned"));
        cut.FindAll("[data-testid='pcp-unavailable']").Should().BeEmpty();
    }

    [Fact]
    public void AsOfDateInPast_ShowsCoverageInForceThen()
    {
        var cut = RenderPanel(DateTime.Today.AddYears(-2));

        cut.WaitForAssertion(() => cut.Find("[data-testid='coverage-status']").TextContent.Should().Contain("ACTIVE COVERAGE"));
        cut.Find("[data-testid='coverage-row']").TextContent.Should().Contain("Old Bronze");
    }

    [Fact]
    public void NoCoverageInForce_ShowsNoActiveCoverage()
    {
        var cut = RenderPanel(DateTime.Today.AddYears(-10));

        cut.WaitForAssertion(() => cut.Find("[data-testid='coverage-status']").TextContent.Should().Contain("NO ACTIVE COVERAGE"));
    }
}
