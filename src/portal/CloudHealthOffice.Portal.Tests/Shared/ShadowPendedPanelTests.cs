using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared;

/// <summary>The pended-claims panel on the Shadow Adjudication page.</summary>
public class ShadowPendedPanelTests : TestContext
{
    private readonly Mock<IShadowPendedClient> _pended = new();

    public ShadowPendedPanelTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_pended.Object);
    }

    [Fact]
    public void Each_pend_reason_shows_what_cho_would_do_and_agreement_with_the_examiner()
    {
        _pended.Setup(p => p.GetAsync(365)).ReturnsAsync(new ShadowPendedAnalysis
        {
            Total = new() { Claims = 30, ChoWouldResolve = 24, ChoWouldPay = 20, ChoWouldDeny = 4, AutoResolveRate = 0.8m, Compared = 20, AgreementRate = 0.95m, StillPended = 3 },
            ByPendReason =
            {
                new()
                {
                    PendReasonCode = "MR01", PendReason = "Medical records requested", Claims = 25, ChoWouldResolve = 22,
                    AutoResolveRate = 0.88m, ChoWouldPend = 3, ExaminerDecided = 20, Compared = 18, Agreed = 18, AgreementRate = 1m,
                    AverageDaysPended = 12.5m, AutomationCandidate = true,
                },
                new()
                {
                    PendReasonCode = "COB", Claims = 5, ChoWouldResolve = 2, AutoResolveRate = 0.4m, Compared = 2, Agreed = 1,
                    AgreementRate = 0.5m, ChoPaidExaminerDenied = 1, StillPended = 3, OldestStillPendedDays = 41,
                },
            },
        });

        var cut = RenderComponent<ShadowPendedPanel>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-pended-total]").TextContent
            .Should().Contain("30 pended claims").And.Contain("80%").And.Contain("95%").And.Contain("3 are still pended"));
        var rows = cut.FindAll("[data-testid=shadow-pended-row]");
        rows.Should().HaveCount(2);
        rows.First().TextContent.Should().Contain("MR01").And.Contain("Medical records requested").And.Contain("18 of 18 (100%)").And.Contain("12.5");
        rows.Last().TextContent.Should().Contain("CHO paid, examiner denied 1").And.Contain("oldest 41 days");
        cut.FindAll("[data-testid=shadow-pended-candidate]").Should().ContainSingle();
    }

    [Fact]
    public void Without_pended_claims_the_panel_says_so()
    {
        _pended.Setup(p => p.GetAsync(It.IsAny<int>())).ReturnsAsync(new ShadowPendedAnalysis());

        var cut = RenderComponent<ShadowPendedPanel>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-pended-empty]").Should().NotBeNull());
    }

    [Fact]
    public void A_service_failure_is_shown_not_thrown()
    {
        _pended.Setup(p => p.GetAsync(It.IsAny<int>())).ThrowsAsync(new ServiceUnavailableException("Claims Service", new HttpRequestException("down")));

        var cut = RenderComponent<ShadowPendedPanel>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-pended-error]").TextContent.Should().Contain("pended claims report"));
    }
}
