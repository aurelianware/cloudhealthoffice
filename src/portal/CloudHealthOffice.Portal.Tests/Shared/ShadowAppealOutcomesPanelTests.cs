using Bunit;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Shared;

/// <summary>The appeal outcomes panel on the Shadow Adjudication page.</summary>
public class ShadowAppealOutcomesPanelTests : TestContext
{
    private readonly Mock<IShadowAppealsClient> _appeals = new();

    public ShadowAppealOutcomesPanelTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Outcomes_are_shown_by_chos_reading_and_by_carc()
    {
        Services.AddSingleton(_appeals.Object);
        _appeals.Setup(a => a.GetSummaryAsync()).ReturnsAsync(new ShadowAppealSummary
        {
            Decided = 50, Overturned = 20, OverturnRate = 0.4m, NotShadowed = 5,
            ByAdvice =
            {
                new() { Key = ShadowAppealAdvice.ChoWouldPay, Decided = 15, Overturned = 12, OverturnRate = 0.8m },
                new() { Key = ShadowAppealAdvice.ChoConfirms, Decided = 30, Overturned = 6, OverturnRate = 0.2m },
            },
            ByDenialCode = { new() { Key = "50", Decided = 25, Overturned = 10, OverturnRate = 0.4m, ChoWouldPay = 8, ChoWouldPayOverturned = 7 } },
        });

        var cut = RenderComponent<ShadowAppealOutcomesPanel>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=shadow-appeal-total]").TextContent
            .Should().Contain("50 decided appeals").And.Contain("40%").And.Contain("5 are on claims not shadowed"));
        cut.Find("[data-testid=shadow-appeal-by-advice]").TextContent
            .Should().Contain("CHO would pay").And.Contain("12 (80%)").And.Contain("CHO confirms").And.Contain("6 (20%)");
        cut.Find("[data-testid=shadow-appeal-by-carc]").TextContent.Should().Contain("50").And.Contain("10 (40%)");
    }

    [Fact]
    public void Without_outcomes_the_panel_says_so()
    {
        Services.AddSingleton(_appeals.Object);
        _appeals.Setup(a => a.GetSummaryAsync()).ReturnsAsync(new ShadowAppealSummary());

        var cut = RenderComponent<ShadowAppealOutcomesPanel>();

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=shadow-appeal-empty]").Should().ContainSingle());
    }

    [Fact]
    public void A_portal_without_the_client_renders_nothing()
    {
        var cut = RenderComponent<ShadowAppealOutcomesPanel>();

        cut.Markup.Trim().Should().BeEmpty();
    }
}
