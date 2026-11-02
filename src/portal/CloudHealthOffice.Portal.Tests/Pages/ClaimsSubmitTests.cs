using System.Text.Json.Nodes;
using Bunit;
using Bunit.TestDoubles;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Manual claim entry: key a professional claim, submit it through the
/// same v1 intake as an 837, and open the new claim.
/// </summary>
public class ClaimsSubmitTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    public ClaimsSubmitTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_user.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _user.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext { UserId = "ex-1", Roles = { "ClaimsExaminer" } });
        _user.Setup(u => u.HasPermission("claims:work")).Returns(true);
    }

    private static void Key(IRenderedComponent<ClaimsSubmit> cut)
    {
        cut.Find("input[data-testid=edit-member]").Change("MEM-1");
        cut.Find("input[data-testid=edit-billing-npi]").Change("1234567893");
        cut.Find("input[data-testid=edit-dx-1]").Change("E11.9");
        cut.Find("input[data-testid=edit-line-1-procedure]").Change("99213");
        cut.Find("input[data-testid=edit-line-1-charge]").Change("125");
    }

    [Fact]
    public void Starts_as_a_blank_professional_claim_with_one_line()
    {
        var cut = RenderComponent<ClaimsSubmit>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-line-1]"));
        cut.FindAll("[data-testid=edit-line-2]").Should().BeEmpty();
        cut.Find("input[data-testid=enter-claim-number]").GetAttribute("value").Should().StartWith("MAN-");
        cut.Markup.Should().NotContain("being finalized");
    }

    [Fact]
    public void Submitting_an_incomplete_claim_lists_what_is_missing_and_sends_nothing()
    {
        var cut = RenderComponent<ClaimsSubmit>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=enter-submit]"));

        cut.Find("[data-testid=enter-submit]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=enter-problems]").TextContent
            .Should().Contain("Member ID is required").And.Contain("Line 1: procedure code is required"));
        _claims.Verify(c => c.SubmitNewClaimAsync(It.IsAny<JsonObject>()), Times.Never);
    }

    [Fact]
    public void A_keyed_claim_is_submitted_and_opened()
    {
        JsonObject? sent = null;
        _claims.Setup(c => c.SubmitNewClaimAsync(It.IsAny<JsonObject>()))
            .Callback((JsonObject c) => sent = c)
            .ReturnsAsync(ClaimCorrectionResult.Ok(string.Empty, "claim-9"));
        var nav = Services.GetRequiredService<FakeNavigationManager>();

        var cut = RenderComponent<ClaimsSubmit>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-line-1]"));
        Key(cut);
        cut.Find("[data-testid=enter-submit]").Click();

        cut.WaitForAssertion(() => nav.Uri.Should().EndWith("/claims/claim-9"));
        sent!["memberId"]!.GetValue<string>().Should().Be("MEM-1");
        sent["claimType"]!.GetValue<int>().Should().Be(1);
        sent["lineOfBusiness"]!.GetValue<int>().Should().Be(1);
        sent["claimFrequencyCode"]!.GetValue<string>().Should().Be("1");
        sent["claimNumber"]!.GetValue<string>().Should().StartWith("MAN-");
        sent["diagnosisCodes"]![0]!["code"]!.GetValue<string>().Should().Be("E119");
        sent["diagnosisCodes"]![0]!["codeQualifier"]!.GetValue<string>().Should().Be("ABK");
        sent["claimLines"]![0]!["procedureCode"]!.GetValue<string>().Should().Be("99213");
        sent["claimLines"]![0]!["chargeAmount"]!.GetValue<decimal>().Should().Be(125);
        sent["totalChargeAmount"]!.GetValue<decimal>().Should().Be(125);
    }

    [Fact]
    public void A_service_refusal_stays_on_the_form()
    {
        _claims.Setup(c => c.SubmitNewClaimAsync(It.IsAny<JsonObject>()))
            .ReturnsAsync(ClaimCorrectionResult.Fail(new[] { "MemberId: MemberId is required" }));

        var cut = RenderComponent<ClaimsSubmit>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=edit-line-1]"));
        Key(cut);
        cut.Find("[data-testid=enter-submit]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=enter-submit-errors]").TextContent.Should().Contain("MemberId is required"));
        cut.Find("input[data-testid=edit-member]").GetAttribute("value").Should().Be("MEM-1");
    }

    [Fact]
    public void Without_claims_work_the_page_is_closed()
    {
        _user.Setup(u => u.HasPermission("claims:work")).Returns(false);

        var cut = RenderComponent<ClaimsSubmit>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("You don't have access to this page"));
    }
}
