using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Shared.Records;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// The payment record page (/payments/{id}/{tab}): one check or EFT, the
/// claims it paid, its PLB adjustments and recoupments, the 835; reached from
/// a claim's Payments tab (payments:read), global search by check number,
/// and the ERA list.
/// </summary>
public class PaymentRecordTests : TestContext
{
    private readonly Mock<IPaymentService> _payments = new();
    private readonly Mock<IClaimsService> _claims = new();
    private readonly Mock<IUserContextService> _user = new();

    private static PaymentView Payment() => new()
    {
        Id = "pay-1", CheckNumber = "EFT0001", PaymentMethod = "ACH", TotalPaymentAmount = 1250.00m,
        PaymentDate = new DateTime(2026, 10, 9), PayeeName = "Lakeside Orthopedics", PayeeNPI = "1234567893", Status = "Posted",
        ClaimPayments =
        [
            new() { ClaimId = "c1", PatientControlNumber = "CLM-1", ClaimStatusCode = "1", ChargeAmount = 2000, PaymentAmount = 1300, MemberId = "M1",
                    ClaimAdjustments = [new() { GroupCode = "CO", ReasonCode = "45", Amount = 700 }] },
        ],
        ProviderAdjustments = [new() { AdjustmentIdentifier = "WO", ReferenceIdentification = "CLM-0", Amount = 50 }],
    };

    public PaymentRecordTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_payments.Object);
        Services.AddSingleton(_claims.Object);
        Services.AddSingleton(_user.Object);
        Services.AddSingleton(Mock.Of<IWorkQueueService>());
        Services.AddSingleton(Mock.Of<IMemberService>());
        Services.AddSingleton(Mock.Of<IProviderService>());
        Services.AddScoped<GlobalSearch>();
        _payments.Setup(p => p.GetPaymentAsync("pay-1")).ReturnsAsync(Payment());
    }

    private void Grant(params string[] permissions) =>
        _user.Setup(u => u.HasPermission(It.IsAny<string>())).Returns((string p) => permissions.Contains(p));

    private IRenderedComponent<PaymentRecordView> Render(string id = "pay-1", string? tab = null) =>
        RenderComponent<PaymentRecordView>(p => p.Add(x => x.PaymentId, id).Add(x => x.Tab, tab).Add(x => x.BaseHref, $"/payments/{id}"));

    [Fact]
    public void Header_shows_the_eft_its_amount_and_payee_with_every_tab_as_a_link()
    {
        var cut = Render();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("EFT EFT0001"));
        cut.Find("[data-testid=payment-amount]").TextContent.Should().Contain("1,250.00");
        cut.Find("[data-testid=payment-payee]").GetAttribute("href").Should().Be("/providers/npi/1234567893");
        cut.FindAll("[data-testid^=record-tab-]").Select(t => t.GetAttribute("href")).Should().Equal(
            "/payments/pay-1/summary", "/payments/pay-1/claims", "/payments/pay-1/adjustments", "/payments/pay-1/era");
    }

    [Fact]
    public void Claims_paid_link_to_the_claim_and_member()
    {
        var cut = Render(tab: "claims");

        cut.WaitForAssertion(() => cut.FindAll("[data-testid=payment-claim-row]").Should().ContainSingle());
        cut.FindAll("[data-testid=payment-claim-row] a.record-link-main").Select(a => a.GetAttribute("href")).Should().Equal("/claims/c1", "/members/M1");
        cut.Find("[data-testid=payment-claim-row]").TextContent.Should().Contain("CO-45");
    }

    [Fact]
    public void Adjustments_name_the_plb_code()
    {
        var cut = Render(tab: "adjustments");

        cut.WaitForAssertion(() => cut.Find("[data-testid=payment-plb-row]").TextContent.Should().Contain("Overpayment recovery"));
    }

    [Fact]
    public void A_check_number_in_the_url_finds_the_payment()
    {
        _payments.Setup(p => p.GetPaymentByCheckNumberAsync("EFT0001")).ReturnsAsync(Payment());

        var cut = Render(id: "EFT0001");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Lakeside Orthopedics"));
    }

    [Fact]
    public void The_claim_shows_its_payments_only_to_payments_readers()
    {
        _claims.Setup(c => c.GetClaimByIdAsync("c1")).ReturnsAsync(new ClaimDetails { ClaimId = "c1", ClaimNumber = "CLM-1", Status = "Paid" });
        _claims.Setup(c => c.GetClaimNotesAsync(It.IsAny<string>())).ReturnsAsync(new List<ClaimNoteView>());
        _payments.Setup(p => p.GetPaymentsForClaimAsync("c1")).ReturnsAsync(new List<PaymentView> { Payment() });

        Grant("claims:read");
        var examiner = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "c1").Add(x => x.Tab, "payments"));
        examiner.WaitForAssertion(() => examiner.Markup.Should().Contain("CLM-1"));
        examiner.FindAll("[data-testid=record-tab-payments]").Should().BeEmpty();
        _payments.Verify(p => p.GetPaymentsForClaimAsync(It.IsAny<string>()), Times.Never);

        Grant("claims:read", "payments:read");
        var finance = RenderComponent<ClaimDetailsNew>(p => p.Add(x => x.ClaimId, "c1").Add(x => x.Tab, "payments"));
        finance.WaitForAssertion(() => finance.FindAll("[data-testid=claim-payment-row]").Should().ContainSingle());
        finance.Find("[data-testid=claim-payment-row] a.record-link-main").GetAttribute("href").Should().Be("/payments/pay-1");
        finance.Find("[data-testid=claim-payment-row]").TextContent.Should().Contain("1,300.00");
    }

    [Fact]
    public async Task Global_search_opens_a_payment_by_check_number_for_payments_readers()
    {
        _payments.Setup(p => p.GetPaymentByCheckNumberAsync("EFT0001")).ReturnsAsync(Payment());
        var search = Services.GetRequiredService<GlobalSearch>();

        Grant("payments:read");
        (await search.SearchAsync("EFT0001")).Jump!.Href.Should().Be("/payments/pay-1");

        Grant("claims:read");
        (await search.SearchAsync("EFT0001")).Hits.Should().NotContain(h => h.Kind == "Payment");
    }
}
