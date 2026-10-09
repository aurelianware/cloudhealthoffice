using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// /claims/{ClaimId} reuses the same component instance when the route
/// changes claim (e.g. following a "Possible duplicates" link). Claim-scoped
/// state must reset and late background loads for the old claim must not
/// land on the new one.
/// </summary>
public class ClaimDetailsClaimSwitchTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();

    public ClaimDetailsClaimSwitchTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(Mock.Of<IUserContextService>());
        JSInterop.Mode = JSRuntimeMode.Loose;

        _claims.Setup(c => c.GetClaimByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClaimDetails
            {
                ClaimId = id,
                ClaimNumber = $"CLM-{id}",
                Status = "Submitted",
                IsEditable = true,
            });
        _claims.Setup(c => c.GetDuplicateMatchesAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ClaimDuplicateMatch>());
    }

    private static AdjudicationTransparencyData Detail(string stepName) => new()
    {
        Steps = new() { new AdjudicationStep { StepNumber = 1, StepName = stepName, Status = "Passed" } },
    };

    [Fact]
    public void Switching_claim_clears_the_draft_note()
    {
        _claims.Setup(c => c.GetAdjudicationDataAsync(It.IsAny<string>()))
            .ReturnsAsync((AdjudicationTransparencyData?)null);
        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-A"));

        cut.Find("textarea").Change("draft note for claim A");
        cut.Find("textarea").GetAttribute("value").Should().Be("draft note for claim A");

        cut.SetParametersAndRender(p => p.Add(x => x.ClaimId, "B"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-B"));
        cut.Find("textarea").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Late_adjudication_result_for_previous_claim_is_dropped()
    {
        var slowA = new TaskCompletionSource<AdjudicationTransparencyData?>();
        _claims.Setup(c => c.GetAdjudicationDataAsync("A")).Returns(slowA.Task);
        _claims.Setup(c => c.GetAdjudicationDataAsync("B")).ReturnsAsync(Detail("Step for claim B"));

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-A"));

        cut.SetParametersAndRender(p => p.Add(x => x.ClaimId, "B"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-B"));
        // Steps render on the Adjudication Pipeline tab.
        cut.FindAll(".mud-tab").First(t => t.TextContent.Contains("Adjudication Pipeline")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Step for claim B"));

        // Claim A's request finishes after the switch; its data must not win.
        cut.InvokeAsync(() => slowA.SetResult(Detail("Step for claim A")));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Step for claim B");
            cut.Markup.Should().NotContain("Step for claim A");
        });
        _claims.Verify(c => c.GetAdjudicationDataAsync("B"), Times.Once);
    }
}
