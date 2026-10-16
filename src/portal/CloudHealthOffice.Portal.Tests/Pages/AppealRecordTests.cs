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
