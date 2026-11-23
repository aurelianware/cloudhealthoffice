using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The appeal record page (/appeals/{id}/{tab}) that replaces the appeal
/// dialog: due clock in the header, member and original decision as links.
/// </summary>
public class AppealRecordTests : TestContext
{
    private readonly Mock<IAppealsService> _appeals = new();

    public AppealRecordTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_appeals.Object);
        Services.AddSingleton(Mock.Of<IUserContextService>());

        _appeals.Setup(a => a.GetAppealByIdAsync("APL-9")).ReturnsAsync(new AppealDetails
        {
            AppealId = "APL-9", MemberId = "M1", MemberName = "Ana Reyes", AppealType = "Claim", Status = "Under Review",
            OriginalDecisionId = "CLM-77", OriginalDecision = "Denied", OriginalDenialReason = "Not medically necessary",
            DueDate = DateTime.Today.AddDays(-2), DaysRemaining = -2, ComplianceStatus = "Overdue",
            Timeline = [new AppealTimelineEvent { EventDate = DateTime.Today.AddDays(-30) }],
        });
    }

    private IRenderedComponent<AppealRecordView> Render(string? tab = null) =>
        RenderComponent<AppealRecordView>(p => p.Add(x => x.AppealId, "APL-9").Add(x => x.Tab, tab).Add(x => x.BaseHref, "/appeals/APL-9"));

    [Fact]
    public void Opens_on_the_case_with_the_due_clock_and_links()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("APL-9"));
        cut.FindAll("[data-testid^=record-tab-]").Select(t => t.GetAttribute("href")).Should().Equal(
            "/appeals/APL-9/case", "/appeals/APL-9/timeline", "/appeals/APL-9/documents");
        cut.Find("[data-testid=appeal-due]").TextContent.Should().Contain("2 day(s) overdue");
        cut.Find("[data-testid=appeal-member]").GetAttribute("href").Should().Be("/members/M1");
        cut.Find("[data-testid=appeal-original-decision]").GetAttribute("href").Should().Be("/claims/CLM-77");
    }

    [Fact]
    public void Without_shadow_evidence_the_case_shows_none()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("APL-9"));
        cut.FindAll("[data-testid=shadow-appeal-evidence]").Should().BeEmpty();
    }

    [Fact]
    public void A_shadowed_claim_shows_chos_reading_on_the_case()
    {
        var shadow = new Mock<IShadowAppealsClient>();
        shadow.Setup(s => s.GetEvidenceAsync("CLM-77")).ReturnsAsync(new ShadowAppealEvidence
        {
            ClaimNumber = "CLM-77", Advice = ShadowAppealAdvice.ChoWouldPay, LikelyOverturn = true,
            AdviceText = "CHO would have paid what the core denied.",
            Result = new()
            {
                CoreOutcome = "Denied", CoreDenialCode = "50", ChoOutcome = "Paid", ChoPaid = 120m,
                ChoStageFindings = { "PriorAuthorization: approved authorization PA-1 covers the service" },
                PriorAuthorizationEvidence = { new() { LineNumber = 1, AuthorizationNumber = "PA-1", Outcome = "Valid", PolicyVersion = "2026.2" } },
            },
            DenialCodeHistory = new() { Key = "50", Decided = 40, Overturned = 26, OverturnRate = 0.65m },
        });
        Services.AddSingleton(shadow.Object);

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-appeal-evidence]").TextContent
            .Should().Contain("CHO would pay").And.Contain("Denied (50)").And.Contain("$120.00")
            .And.Contain("PA-1").And.Contain("Policy 2026.2").And.Contain("Advisory"));
        cut.Find("[data-testid=shadow-appeal-history]").TextContent.Should().Contain("26 of 40 overturned (65%)");
    }

    [Fact]
    public void Honours_the_tab_in_the_url()
    {
        var cut = Render("timeline");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=appeal-tab-timeline]").Should().ContainSingle());
        cut.FindAll("[data-testid=appeal-tab-case]").Should().BeEmpty();
    }

    [Fact]
    public void A_missing_appeal_says_so()
    {
        var cut = RenderComponent<AppealRecordView>(p => p.Add(x => x.AppealId, "nope").Add(x => x.BaseHref, "/appeals/nope"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=appeal-missing]"));
    }
}
