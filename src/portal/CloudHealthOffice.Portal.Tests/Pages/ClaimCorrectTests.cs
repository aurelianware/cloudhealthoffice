using System.Text.Json.Nodes;
using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Examiner correction of a finalized claim: edit, say why, review what
/// changed, submit as an adjustment, land on the new version.
/// </summary>
public class ClaimCorrectTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    public ClaimCorrectTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(_user.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;

        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "sup-1", Roles = { "ClaimsSupervisor" } });
        _user.Setup(u => u.HasPermission("claims:adjust")).Returns(true);
        _claims.Setup(c => c.GetDuplicateMatchesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimDuplicateMatch>());
        _claims.Setup(c => c.GetAdjudicationDataAsync(It.IsAny<string>())).ReturnsAsync((AdjudicationTransparencyData?)null);
        _claims.Setup(c => c.GetClaimNotesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimNoteView>());
    }

    private void Claim(string status) =>
        _claims.Setup(c => c.GetClaimByIdAsync("claim-1"))
            .ReturnsAsync(new ClaimDetails { ClaimId = "claim-1", ClaimNumber = "CLM-1", Status = status, ClaimType = "Professional" });

    private void Draft() =>
        _claims.Setup(c => c.GetCorrectionDraftAsync("claim-1")).ReturnsAsync(JsonNode.Parse("""
            {
              "memberId": "MEM-1", "billingProviderNPI": "1234567893", "placeOfServiceCode": "11",
              "diagnosisCodes": [ { "code": "E119", "codeQualifier": "ABK", "pointerNumber": 1 } ],
              "claimLines": [ { "lineNumber": 1, "procedureCode": "99213", "modifiers": [], "diagnosisPointers": [1],
                                "units": 3, "chargeAmount": 300, "serviceDateFrom": "2026-10-01T00:00:00Z",
                                "serviceDateTo": "2026-10-01T00:00:00Z" } ]
            }
            """)!.AsObject());

    private IRenderedComponent<ClaimCorrect> Render() =>
        RenderComponent<ClaimCorrect>(p => p.Add(x => x.ClaimId, "claim-1"));

    [Fact]
    public void A_paid_claim_opens_in_the_editor()
    {
        Claim("Paid");
        Draft();

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-line-1]"));
        cut.Markup.Should().Contain("Correct claim CLM-1");
        cut.Find("input[data-testid=edit-line-1-procedure]").GetAttribute("value").Should().Be("99213");
    }

    [Fact]
    public void A_pended_claim_cant_be_corrected_here()
    {
        Claim("Pended");

        var cut = Render();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correct-not-allowed]"));
        _claims.Verify(c => c.GetCorrectionDraftAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Without_claims_adjust_the_page_is_closed()
    {
        _user.Setup(u => u.HasPermission("claims:adjust")).Returns(false);
        Claim("Paid");
        Draft();

        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("You don't have access to this page"));
        cut.FindAll("[data-testid=edit-line-1]").Should().BeEmpty();
    }

    [Fact]
    public void Review_lists_the_change_and_submit_sends_it_then_opens_the_new_version()
    {
        Claim("Paid");
        Draft();
        JsonObject? sent = null;
        _claims.Setup(c => c.SubmitCorrectionAsync("claim-1", "Units should be 1", null, It.IsAny<JsonObject>(), It.IsAny<string>()))
            .Callback((string _, string _, string? _, JsonObject claim, string _) => sent = claim)
            .ReturnsAsync(ClaimCorrectionResult.Ok("adj-1", "claim-2"));
        var nav = Services.GetRequiredService<FakeNavigationManager>();

        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-line-1]"));

        cut.Find("input[data-testid=edit-line-1-units]").Change("1");
        cut.Find("input[data-testid=correct-reason]").Change("Units should be 1");
        cut.Find("[data-testid=correct-review-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correct-changes]").TextContent.Should().Contain("Line 1:"));
        cut.Find("[data-testid=correct-submit]").Click();

        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/claims/claim-2"));
        sent!["claimLines"]![0]!["units"]!.GetValue<decimal>().Should().Be(1);
    }

    [Fact]
    public void Nothing_changed_means_nothing_to_submit()
    {
        Claim("Denied");
        Draft();

        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-line-1]"));
        cut.Find("input[data-testid=correct-reason]").Change("Recheck");
        cut.Find("[data-testid=correct-review-button]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correct-no-changes]"));
        cut.Find("[data-testid=correct-submit]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void A_refusal_is_shown_and_the_examiner_stays_on_the_page()
    {
        Claim("Paid");
        Draft();
        _claims.Setup(c => c.SubmitCorrectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<JsonObject>(), It.IsAny<string>()))
            .ReturnsAsync(ClaimCorrectionResult.Fail(new[] { "Chain claim-1 already has an in-flight adjustment adj-0" }));

        var cut = Render();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-line-1]"));
        cut.Find("input[data-testid=edit-line-1-units]").Change("1");
        cut.Find("input[data-testid=correct-reason]").Change("fix");
        cut.Find("[data-testid=correct-review-button]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=correct-submit]"));
        cut.Find("[data-testid=correct-submit]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=correct-submit-errors]").TextContent.Should().Contain("in-flight adjustment"));
    }

    [Theory]
    [InlineData("Paid", true, true)]
    [InlineData("Denied", true, true)]
    [InlineData("Paid", false, false)]
    [InlineData("Pended", true, false)]
    [InlineData("Voided", true, false)]
    public void Claim_page_offers_Correct_claim_only_on_correctable_claims(string status, bool canAdjust, bool shown)
    {
        _user.Setup(u => u.HasPermission("claims:adjust")).Returns(canAdjust);
        Claim(status);

        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "claim-1"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-1"));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=correct-claim]").Count.Should().Be(shown ? 1 : 0));
    }
}
