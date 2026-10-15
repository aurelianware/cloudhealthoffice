using System.Text.Json;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The Pay/Deny lifecycle in the portal: wire statuses (Approved, Deny,
/// Paid, Denied) are shown as Pay / Deny / Paid / Denied with a phase hint,
/// the converter reads the new Deny value, and the detail header offers the
/// actions that fit the phase.
/// </summary>
public class ClaimStatusDisplayTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("Approved", "Pay")]
    [InlineData("Deny", "Deny")]
    [InlineData("Paid", "Paid")]
    [InlineData("Denied", "Denied")]
    [InlineData("PartiallyPaid", "Paid with line denials")]
    [InlineData("InAdjudication", "In Adjudication")]
    [InlineData("Pended", "Pended")]
    [InlineData("Voided", "Voided")]
    public void Label_ShowsTheBusinessName(string wire, string label) =>
        ClaimStatusDisplay.Label(wire).Should().Be(label);

    [Theory]
    [InlineData("Approved", ClaimStatusDisplay.AwaitingPaymentCycle)]
    [InlineData("Deny", ClaimStatusDisplay.AwaitingPaymentCycle)]
    [InlineData("Paid", ClaimStatusDisplay.Finalized)]
    [InlineData("Denied", ClaimStatusDisplay.Finalized)]
    [InlineData("Voided", "Reversed")]
    [InlineData("Pended", null)]
    public void PhaseHint_SeparatesAdjudicatedFromFinalized(string wire, string? hint) =>
        ClaimStatusDisplay.PhaseHint(wire).Should().Be(hint);

    [Theory]
    [InlineData(10, "Deny")]
    [InlineData(6, "Denied")]
    [InlineData(5, "Approved")]
    [InlineData(7, "Paid")]
    public void Converter_ReadsNumericStatuses_IncludingDeny(int wire, string expected) =>
        JsonSerializer.Deserialize<ClaimSummary>($$"""{"status":{{wire}}}""", JsonOpts)!.Status.Should().Be(expected);

    [Fact]
    public void Converter_ReadsTheDenyString() =>
        JsonSerializer.Deserialize<ClaimSummary>("""{"status":"Deny"}""", JsonOpts)!.Status.Should().Be("Deny");

    [Fact]
    public void FilterOptions_AreThePhaseStatuses()
    {
        ClaimStatusDisplay.FilterOptions.Select(o => o.Label).Should().Contain(
            new[] { "Pended", "In Adjudication", "Pay", "Deny", "Paid", "Denied", "Voided" });
        ClaimStatusDisplay.FilterOptions.Should().Contain(("Approved", "Pay"));
        ClaimStatusDisplay.FilterOptions.Should().Contain(("Deny", "Deny"));
        ClaimStatusDisplay.FilterOptions.Select(o => o.Value).Should().NotContain("PartiallyPaid");
    }

    [Fact]
    public void PaidClaim_WithADeniedLine_IsShownAsPaidWithLineDenials()
    {
        var lines = JsonSerializer.Deserialize<List<ClaimServiceLine>>(
            """
            [
              {"lineNumber":1,"chargeAmount":100,"adjudicationResult":{"allowedAmount":80,"paidAmount":80,"patientResponsibility":0}},
              {"lineNumber":2,"chargeAmount":50,"adjudicationResult":{"allowedAmount":0,"paidAmount":0,"patientResponsibility":0}}
            ]
            """, JsonOpts)!;

        ClaimStatusDisplay.Label("Paid", lines).Should().Be("Paid with line denials");
        ClaimStatusDisplay.Label("Approved", lines).Should().Be("Pay with line denials");
        ClaimStatusDisplay.Label("Paid", lines.Take(1)).Should().Be("Paid");
    }

    [Fact]
    public void ADeductibleOnlyLine_IsNotADenial()
    {
        var line = JsonSerializer.Deserialize<ClaimServiceLine>(
            """{"lineNumber":1,"chargeAmount":100,"adjudicationResult":{"allowedAmount":100,"paidAmount":0,"patientResponsibility":100}}""",
            JsonOpts)!;

        line.IsDenied.Should().BeFalse();
    }

    [Theory]
    [InlineData("Pended", true)]
    [InlineData("Approved", true)]
    [InlineData("Deny", true)]
    [InlineData("Paid", false)]
    [InlineData("Denied", false)]
    [InlineData("Voided", false)]
    public void Reprocessable_IsPendedPayOrDeny(string wire, bool expected) =>
        ClaimStatusDisplay.IsReprocessable(wire).Should().Be(expected);
}

/// <summary>The claim detail header: label, phase hint and the actions that fit the phase.</summary>
public class ClaimDetailsPhaseHeaderTests : TestContext
{
    private readonly Mock<IClaimsService> _claims = new();

    public ClaimDetailsPhaseHeaderTests()
    {
        Services.AddMudServices();
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(Mock.Of<IUserContextService>());
        JSInterop.Mode = JSRuntimeMode.Loose;
        _claims.Setup(c => c.GetDuplicateMatchesAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ClaimDuplicateMatch>());
        _claims.Setup(c => c.GetAdjudicationDataAsync(It.IsAny<string>()))
            .ReturnsAsync((AdjudicationTransparencyData?)null);
    }

    private IRenderedComponent<ClaimDetailsNew> Render(string status)
    {
        _claims.Setup(c => c.GetClaimByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => new ClaimDetails { ClaimId = id, ClaimNumber = $"CLM-{id}", Status = status });
        var cut = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "A"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("CLM-A"));
        return cut;
    }

    private static List<string> Buttons(IRenderedComponent<ClaimDetailsNew> cut) =>
        cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void DenyClaim_ShowsDeny_AwaitingPaymentCycle_WithPayAndDenyActions()
    {
        var cut = Render("Deny");

        cut.Find("[data-testid=claim-phase-hint]").TextContent.Should().Be(ClaimStatusDisplay.AwaitingPaymentCycle);
        Buttons(cut).Should().Contain(new[] { "Pay", "Deny" });
        Buttons(cut).Should().NotContain("Initiate Reversal");
    }

    [Fact]
    public void ApprovedClaim_IsShownAsPay()
    {
        var cut = Render("Approved");

        cut.Markup.Should().Contain(">Pay<", "the chip label").And.Contain(ClaimStatusDisplay.AwaitingPaymentCycle);
    }

    [Fact]
    public void PaidClaim_IsFinalized_OffersReversal_NotPayOrDeny()
    {
        var cut = Render("Paid");

        cut.Find("[data-testid=claim-phase-hint]").TextContent.Should().Be(ClaimStatusDisplay.Finalized);
        Buttons(cut).Should().Contain("Initiate Reversal");
        Buttons(cut).Should().NotContain(new[] { "Pay", "Deny" });
    }

    [Fact]
    public void DeniedClaim_IsFinalized_NoActionsButAdjustmentHint()
    {
        var cut = Render("Denied");

        cut.Find("[data-testid=claim-phase-hint]").TextContent.Should().Be(ClaimStatusDisplay.Finalized);
        Buttons(cut).Should().NotContain(new[] { "Pay", "Deny", "Initiate Reversal" });
        cut.Markup.Should().Contain("correct it with an adjustment");
    }
}
