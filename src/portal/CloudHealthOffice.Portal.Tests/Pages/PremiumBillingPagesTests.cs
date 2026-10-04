using System.Net;
using System.Text.Json;
using Bunit;
using CloudHealthOffice.Portal.Pages;
using CloudHealthOffice.Portal.Services;
using CloudHealthOffice.Portal.Tests.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace CloudHealthOffice.Portal.Tests.Pages;

/// <summary>
/// Premium billing pages rendered with the real portal clients over a stub of
/// premium-billing-service and sponsor-service answering in their real shapes.
/// </summary>
public abstract class BillingPageTestBase : TestContext
{
    protected readonly BillingApiStub Api = new();

    protected BillingPageTestBase()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Services:BillingService"] = "http://premium-billing-service/api",
            ["Services:SponsorService"] = "http://sponsor-service/api/v1",
        }).Build();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton(new HttpClient(Api));
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<IPremiumBillingService, PremiumBillingService>();
        Services.AddSingleton<ISponsorBankAccountService, SponsorBankAccountService>();
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected void SignIn(string userId, params string[] permissions)
    {
        var granted = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
        var users = new Mock<IUserContextService>();
        users.Setup(u => u.GetCurrentUserAsync()).ReturnsAsync(new UserContext
        {
            UserId = userId, TenantId = "t1", Roles = new List<string> { "SomeRole" }, Permissions = granted
        });
        users.Setup(u => u.HasPermission(It.IsAny<string>())).Returns((string p) => granted.Contains(p));
        Services.AddSingleton(users.Object);
    }

    /// <summary>Render a page at a URL, so its [SupplyParameterFromQuery] parameters are filled.</summary>
    protected IRenderedComponent<T> RenderAt<T>(string uri) where T : IComponent
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        return RenderComponent<T>();
    }

    protected static void Type(IRenderedFragment cut, string testId, string value)
        => cut.Find($"[data-testid={testId}] input").Change(value);

    protected static void Click(IRenderedFragment cut, string testId)
        => cut.Find($"[data-testid={testId}]").Click();

    protected static bool Has(IRenderedFragment cut, string testId)
        => cut.FindAll($"[data-testid={testId}]").Count > 0;

    protected static JsonElement Json(BillingApiStub.Call call) => JsonDocument.Parse(call.Body!).RootElement;
}

public class PremiumBillingRunsPageTests : BillingPageTestBase
{
    public PremiumBillingRunsPageTests()
    {
        Api.On("GET", "/api/v1/billing-runs", HttpStatusCode.OK, $"[{BillingApiShapes.RunPending},{BillingApiShapes.RunCompleted}]");
    }

    private IRenderedComponent<PremiumBilling> Render()
    {
        var cut = RenderComponent<PremiumBilling>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("BR-2026-02-001"));
        return cut;
    }

    [Fact]
    public void Reader_SeesRuns_ButNoCreateExecuteOrCancel()
    {
        SignIn("fa-1", "billing:read");

        var cut = Render();

        Api.CallsTo("GET", "/api/v1/billing-runs").Should().ContainSingle();
        cut.Markup.Should().Contain("BR-2026-03-001");
        Has(cut, "create-run-form").Should().BeFalse();
        Has(cut, "run-execute-run-1").Should().BeFalse();
        Has(cut, "run-cancel-run-1").Should().BeFalse();
    }

    [Fact]
    public void Runner_CreatesARun_WithThePeriodAndGroups()
    {
        SignIn("fin-1", "billing:read", "billing:run");
        Api.On("POST", "/api/v1/billing-runs", HttpStatusCode.Created, BillingApiShapes.RunPending);
        var cut = Render();

        Type(cut, "run-period", "2026-03");
        Type(cut, "run-groups", "G1, G2,G1");
        Type(cut, "run-description", "March premiums");
        Click(cut, "create-run");

        cut.WaitForAssertion(() => Api.CallsTo("POST", "/api/v1/billing-runs").Should().ContainSingle());
        var body = Json(Api.CallsTo("POST", "/api/v1/billing-runs").Single());
        body.GetProperty("billingPeriod").GetString().Should().StartWith("2026-03-01");
        body.GetProperty("criteria").GetProperty("groupNumbers").EnumerateArray().Select(e => e.GetString()).Should().Equal("G1", "G2");
        body.GetProperty("description").GetString().Should().Be("March premiums");
        cut.WaitForAssertion(() => Has(cut, "run-detail").Should().BeTrue());
    }

    [Fact]
    public void Runner_ExecutesAPendingRun_AndSeesErrorsAndNeedsAttention()
    {
        SignIn("fin-1", "billing:read", "billing:run");
        Api.On("POST", "/api/v1/billing-runs/run-1/execute", HttpStatusCode.OK, BillingApiShapes.RunCompleted);
        var cut = Render();

        Click(cut, "run-execute-run-1");

        cut.WaitForAssertion(() => Has(cut, "run-detail").Should().BeTrue());
        Api.CallsTo("POST", "/api/v1/billing-runs/run-1/execute").Should().ContainSingle();
        cut.Find("[data-testid=run-errors]").TextContent.Should().Contain("coverage-service answered 503");
        cut.Find("[data-testid=run-warnings]").TextContent.Should().Contain("Needs attention")
            .And.Contain("G3 has no approved bank account");
    }

    [Fact]
    public void ViewingARun_ShowsItsResults()
    {
        SignIn("fa-1", "billing:read");
        Api.On("GET", "/api/v1/billing-runs/run-2", HttpStatusCode.OK, BillingApiShapes.RunCompleted);
        var cut = Render();

        Click(cut, "run-view-run-2");

        cut.WaitForAssertion(() => Has(cut, "run-detail").Should().BeTrue());
        var detail = cut.Find("[data-testid=run-detail]").TextContent;
        detail.Should().Contain("G1, G2").And.Contain("maker-2");
        cut.Find("[data-testid=run-warnings]").TextContent.Should().Contain("G3");
    }

    [Fact]
    public void BadPeriod_IsRefusedBeforeCallingTheService()
    {
        SignIn("fin-1", "billing:read", "billing:run");
        var cut = Render();

        Type(cut, "run-period", "March");
        Click(cut, "create-run");

        cut.Find("[data-testid=billing-error]").TextContent.Should().Contain("YYYY-MM");
        Api.CallsTo("POST", "/api/v1/billing-runs").Should().BeEmpty();
    }
}

public class PremiumBillingInvoicesPageTests : BillingPageTestBase
{
    public PremiumBillingInvoicesPageTests()
    {
        Api.On("GET", "/api/v1/premium-invoices", HttpStatusCode.OK, $"[{BillingApiShapes.Invoice()}]")
            .On("GET", "/api/v1/premium-invoices/aging-report", HttpStatusCode.OK, BillingApiShapes.Aging)
            .On("GET", "/api/v1/premium-invoices/inv-1", HttpStatusCode.OK, BillingApiShapes.Invoice());
    }

    private IRenderedComponent<PremiumBillingInvoices> RenderAndOpen()
    {
        var cut = RenderComponent<PremiumBillingInvoices>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("INV-G1-2026-02"));
        Click(cut, "invoice-view-inv-1");
        cut.WaitForAssertion(() => Has(cut, "invoice-detail").Should().BeTrue());
        return cut;
    }

    [Fact]
    public void Reader_ListsAndViewsInvoices_WithoutLedgerActions()
    {
        SignIn("fa-1", "billing:read");

        var cut = RenderAndOpen();

        Api.Calls.Should().Contain(c => c.PathAndQuery == "/api/v1/premium-invoices?page=1&pageSize=50");
        cut.Find("[data-testid=aging]").TextContent.Should().Contain("1,450");
        cut.Find("[data-testid=invoice-detail]").TextContent.Should().Contain("Pat Doe").And.Contain("Goodwill");
        Has(cut, "record-payment").Should().BeFalse();
        Has(cut, "void-invoice").Should().BeFalse();
        Has(cut, "mark-sent").Should().BeFalse();
    }

    [Fact]
    public void FinanceWriter_RecordsAPayment()
    {
        SignIn("fin-1", "billing:read", "finance:write");
        Api.On("POST", "/api/v1/premium-invoices/inv-1/payments", HttpStatusCode.OK, BillingApiShapes.Invoice(status: "Paid"));
        var cut = RenderAndOpen();

        Type(cut, "payment-date", "2026-02-20");
        Type(cut, "payment-reference", "TRACE-77");
        Click(cut, "record-payment");

        cut.WaitForAssertion(() => Api.CallsTo("POST", "/api/v1/premium-invoices/inv-1/payments").Should().ContainSingle());
        var body = Json(Api.CallsTo("POST", "/api/v1/premium-invoices/inv-1/payments").Single());
        body.GetProperty("amount").GetDecimal().Should().Be(450m, "the balance due is the default amount");
        body.GetProperty("paymentDate").GetString().Should().StartWith("2026-02-20");
        body.GetProperty("paymentMethod").GetString().Should().Be("ACH");
        body.GetProperty("referenceNumber").GetString().Should().Be("TRACE-77");
    }

    [Fact]
    public void FinanceWriter_VoidsWithAReason()
    {
        SignIn("fin-1", "billing:read", "finance:write");
        Api.On("POST", "/api/v1/premium-invoices/inv-1/void", HttpStatusCode.OK, BillingApiShapes.Invoice(status: "Voided"));
        var cut = RenderAndOpen();

        Click(cut, "void-invoice");
        cut.Find("[data-testid=billing-error]").TextContent.Should().Contain("reason");
        Api.CallsTo("POST", "/api/v1/premium-invoices/inv-1/void").Should().BeEmpty();

        Type(cut, "void-reason", "Issued twice");
        Click(cut, "void-invoice");

        cut.WaitForAssertion(() => Api.CallsTo("POST", "/api/v1/premium-invoices/inv-1/void").Should().ContainSingle());
        Json(Api.CallsTo("POST", "/api/v1/premium-invoices/inv-1/void").Single()).GetProperty("reason").GetString()
            .Should().Be("Issued twice");
        cut.WaitForAssertion(() => cut.Find("[data-testid=invoice-detail]").TextContent.Should().Contain("Voided"));
    }

    [Fact]
    public void RunFilter_ReadsTheRunsInvoices()
    {
        SignIn("fa-1", "billing:read");
        Api.On("GET", "/api/v1/billing-runs/run-2", HttpStatusCode.OK, BillingApiShapes.RunCompleted)
            .On("GET", "/api/v1/premium-invoices/inv-2", HttpStatusCode.OK, BillingApiShapes.Invoice("inv-2"));

        var cut = RenderAt<PremiumBillingInvoices>("/premium-billing/invoices?run=run-2");

        cut.WaitForAssertion(() => Has(cut, "run-filter").Should().BeTrue());
        cut.WaitForAssertion(() => Api.CallsTo("GET", "/api/v1/premium-invoices/inv-2").Should().ContainSingle());
        Api.CallsTo("GET", "/api/v1/premium-invoices/inv-1").Should().ContainSingle();
        Api.Calls.Should().NotContain(c => c.PathAndQuery.StartsWith("/api/v1/premium-invoices?"));
    }
}

public class PremiumBillingDelinquencyPageTests : BillingPageTestBase
{
    public PremiumBillingDelinquencyPageTests()
    {
        Api.On("GET", "/api/v1/premium-invoices", HttpStatusCode.OK,
            $"[{BillingApiShapes.Invoice("inv-7", "Delinquent", BillingApiShapes.FailedSuspension)}]");
    }

    [Fact]
    public void Reader_SeesFailedSuspensionsAwaitingRetry_ButCannotRun()
    {
        SignIn("fa-1", "billing:read");

        var cut = RenderComponent<PremiumBillingDelinquency>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=pending-retries]").TextContent.Should().Contain("sponsor-service unavailable"));
        Api.Calls.Should().Contain(c => c.PathAndQuery.Contains("status=Delinquent"));
        cut.Find("[data-testid=pending-retries]").TextContent.Should().Contain("503");
        Has(cut, "delinquency-run-panel").Should().BeFalse();
    }

    [Fact]
    public void FinanceWriter_Runs_AndSeesSuspensionFailuresFromThe502()
    {
        SignIn("fin-1", "billing:read", "finance:write");
        Api.On("POST", "/api/v1/premium-invoices/process-delinquencies", HttpStatusCode.BadGateway, BillingApiShapes.DelinquencyFailed);
        var cut = RenderComponent<PremiumBillingDelinquency>();
        cut.WaitForAssertion(() => Has(cut, "delinquency-start").Should().BeTrue());

        Click(cut, "delinquency-start");
        Api.CallsTo("POST", "/api/v1/premium-invoices/process-delinquencies").Should().BeEmpty("it asks first");
        Click(cut, "delinquency-confirm");

        cut.WaitForAssertion(() => Has(cut, "delinquency-result").Should().BeTrue());
        Api.CallsTo("POST", "/api/v1/premium-invoices/process-delinquencies").Should().ContainSingle();
        cut.Find("[data-testid=delinquent-count]").TextContent.Should().Be("3");
        cut.Find("[data-testid=suspension-failed-alert]").TextContent.Should().Contain("FAILED");
        cut.Find("[data-testid=suspension-failures]").TextContent.Should().Contain("G7").And.Contain("403")
            .And.Contain("Forbidden by sponsor-service");
        Has(cut, "billing-error").Should().BeFalse("a 502 with results is not a generic error");
    }

    [Fact]
    public void FinanceWriter_SuccessfulRun_ShowsTheMessage()
    {
        SignIn("fin-1", "billing:read", "finance:write");
        Api.On("POST", "/api/v1/premium-invoices/process-delinquencies", HttpStatusCode.OK, BillingApiShapes.DelinquencyOk);
        var cut = RenderComponent<PremiumBillingDelinquency>();
        cut.WaitForAssertion(() => Has(cut, "delinquency-start").Should().BeTrue());

        Click(cut, "delinquency-start");
        Click(cut, "delinquency-confirm");

        cut.WaitForAssertion(() => cut.Find("[data-testid=delinquency-result]").TextContent.Should().Contain("2 invoices marked delinquent"));
        Has(cut, "suspension-failed-alert").Should().BeFalse();
    }
}

public class PremiumBillingEftPageTests : BillingPageTestBase
{
    public PremiumBillingEftPageTests()
    {
        Api.On("GET", "/api/v1/billing-runs", HttpStatusCode.OK, $"[{BillingApiShapes.RunPending},{BillingApiShapes.RunCompleted}]")
            .On("GET", "/api/v1/eft/drafts/invoice/inv-1", HttpStatusCode.OK, $"[{BillingApiShapes.Draft("inv-1")}]")
            .On("GET", "/api/v1/eft/drafts/invoice/inv-2", HttpStatusCode.OK, "[]");
    }

    private IRenderedComponent<PremiumBillingEft> RenderWithRun()
    {
        var cut = RenderComponent<PremiumBillingEft>();
        cut.WaitForAssertion(() => Has(cut, "eft-run-run-2").Should().BeTrue());
        Click(cut, "eft-run-run-2");
        cut.WaitForAssertion(() => cut.Find("[data-testid=eft-drafts]").TextContent.Should().Contain("INV-inv-1"));
        return cut;
    }

    [Fact]
    public void Reader_SeesTheRunsDrafts_Masked_AndNoReleaseActions()
    {
        SignIn("fin-1", "billing:read");

        var cut = RenderWithRun();

        Has(cut, "eft-run-run-1").Should().BeFalse("only completed runs have invoices to debit");
        Api.CallsTo("GET", "/api/v1/eft/drafts/invoice/inv-1").Should().ContainSingle();
        Api.CallsTo("GET", "/api/v1/eft/drafts/invoice/inv-2").Should().ContainSingle();
        var drafts = cut.Find("[data-testid=eft-drafts]").TextContent;
        drafts.Should().Contain("••••6789").And.Contain("••••0019").And.Contain("Submitted");
        Has(cut, "eft-release").Should().BeFalse();
    }

    [Fact]
    public void PaymentsReadOnly_LooksUpAnInvoicesDrafts()
    {
        SignIn("ar-1", "payments:read");

        var cut = RenderComponent<PremiumBillingEft>();
        Type(cut, "eft-invoice-lookup", "inv-1");
        Click(cut, "eft-invoice-lookup-go");

        cut.WaitForAssertion(() => cut.Find("[data-testid=eft-drafts]").TextContent.Should().Contain("INV-inv-1"));
        Api.CallsTo("GET", "/api/v1/billing-runs").Should().BeEmpty("billing-run reads need billing:read");
        Has(cut, "eft-release").Should().BeFalse();
    }

    [Fact]
    public void Approver_ReleasesTheRun_AndSeesNeedsAttention()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/eft/drafts/batch", HttpStatusCode.OK, BillingApiShapes.BatchWithAttention);
        var cut = RenderWithRun();

        Click(cut, "eft-release-run");

        cut.WaitForAssertion(() => Has(cut, "eft-batch-result").Should().BeTrue());
        var body = Json(Api.CallsTo("POST", "/api/v1/eft/drafts/batch").Single());
        body.GetProperty("billingRunId").GetString().Should().Be("run-2");
        body.TryGetProperty("method", out _).Should().BeFalse();
        var attention = cut.Find("[data-testid=eft-needs-attention]");
        attention.TextContent.Should().Contain("G3").And.Contain("No approved bank account");
        attention.QuerySelector("a")!.GetAttribute("href").Should().Be("/premium-billing/sponsor-bank-accounts?group=G3");
    }

    [Fact]
    public void Approver_RefusedForSeparationOfDuties_SeesItClearly()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/eft/drafts/batch", HttpStatusCode.Forbidden, BillingApiShapes.SeparationOfDuties);
        var cut = RenderWithRun();

        Click(cut, "eft-release-run");

        cut.WaitForAssertion(() => Has(cut, "billing-error-sod").Should().BeTrue());
        cut.Find("[data-testid=billing-error-sod]").TextContent.Should().Contain("Separation of duties")
            .And.Contain("different user").And.Contain("executed billing run BR-2026-02-001");
    }

    [Fact]
    public void RunsOwnExecutor_IsNotOfferedTheRelease()
    {
        // maker-2 executed run-2: the service would refuse, so the button is replaced by the reason.
        SignIn("maker-2", "billing:read", "payments:approve");

        var cut = RenderWithRun();

        Has(cut, "eft-release-run").Should().BeFalse();
        cut.Find("[data-testid=eft-sod-hint]").TextContent.Should().Contain("separation of duties");
    }

    [Fact]
    public void Approver_ReleasesOneInvoice()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/eft/drafts", HttpStatusCode.Created, BillingApiShapes.Draft("inv-1", "Pending"));
        var cut = RenderComponent<PremiumBillingEft>();
        cut.WaitForAssertion(() => Has(cut, "eft-release").Should().BeTrue());

        Type(cut, "eft-release-invoice-id", "inv-1");
        Click(cut, "eft-release-invoice");

        cut.WaitForAssertion(() => Has(cut, "eft-single-result").Should().BeTrue());
        Json(Api.CallsTo("POST", "/api/v1/eft/drafts").Single()).GetProperty("invoiceId").GetString().Should().Be("inv-1");
    }

    [Fact]
    public void Approver_SendsNacha_SeesTheMaskedSummaryAndReceipt_AndNothingIsDownloaded()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/eft/nacha/generate", HttpStatusCode.OK, BillingApiShapes.NachaResult);
        var cut = RenderWithRun();

        Click(cut, "eft-generate-nacha");

        cut.WaitForAssertion(() => Has(cut, "eft-nacha-result").Should().BeTrue());
        Api.CallsTo("POST", "/api/v1/eft/nacha/generate").Should().ContainSingle();
        var result = cut.Find("[data-testid=eft-nacha-result]").TextContent;
        result.Should().Contain("NACHA-20260216").And.Contain("1 entry").And.Contain("Sent to the bank");
        cut.Find("[data-testid=eft-nacha-result-receipt]").TextContent.Should()
            .Contain("sftp://sftp.bank.example:22/inbound").And.Contain("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
        cut.Find("[data-testid=eft-nacha-result-entries]").TextContent.Should().Contain("G1").And.Contain("••••6789").And.Contain("450.00");
        cut.Find("[data-testid=eft-needs-attention]").TextContent.Should().Contain("G4");
        // No file and no download: the service sent it to the bank.
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "downloadBase64File");
        cut.Markup.Should().NotContain("download", "there is no download link or action");
        cut.Markup.Should().NotContain(BillingApiShapes.FullAccountNumber);
        cut.Markup.Should().NotContain(BillingApiShapes.FullRoutingNumber);
    }

    [Fact]
    public void BankUnreachable_ShowsAwaitingRetrieval_WithTheNote_AndASecondApproverCanRetry()
    {
        SignIn("approver-2", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/eft/nacha/generate", HttpStatusCode.OK, BillingApiShapes.NachaAwaitingRetrieval);
        Api.On("GET", "/api/v1/eft/nacha/held", HttpStatusCode.OK, BillingApiShapes.HeldFiles);
        Api.On("POST", "/api/v1/eft/nacha/held/NACHA-HELD0001/retry", HttpStatusCode.OK, BillingApiShapes.NachaResult);
        var cut = RenderWithRun();

        Click(cut, "eft-generate-nacha");

        cut.WaitForAssertion(() => Has(cut, "eft-nacha-result-error").Should().BeTrue());
        cut.Find("[data-testid=eft-nacha-result]").TextContent.Should().Contain("Awaiting retrieval");
        cut.Find("[data-testid=eft-nacha-result-error]").TextContent.Should().Contain("platform admin must retrieve it");
        var awaiting = cut.Find("[data-testid=eft-awaiting-retrieval]").TextContent;
        awaiting.Should().Contain("NACHA-HELD0001").And.Contain("approver-1");
        cut.Find("[data-testid=eft-awaiting-retrieval-note]").TextContent.Should().Contain("A platform admin must retrieve the file");

        Click(cut, "eft-awaiting-retrieval-retry-NACHA-HELD0001");

        cut.WaitForAssertion(() => cut.Find("[data-testid=eft-nacha-result]").TextContent.Should().Contain("Sent to the bank"));
        Api.CallsTo("POST", "/api/v1/eft/nacha/held/NACHA-HELD0001/retry").Should().ContainSingle();
        cut.Markup.Should().NotContain(BillingApiShapes.FullAccountNumber);
    }

    [Fact]
    public void TheReleaser_IsNotOfferedTheRetry()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("GET", "/api/v1/eft/nacha/held", HttpStatusCode.OK, BillingApiShapes.HeldFiles);

        var cut = RenderComponent<PremiumBillingEft>();

        cut.WaitForAssertion(() => Has(cut, "eft-awaiting-retrieval").Should().BeTrue());
        Has(cut, "eft-awaiting-retrieval-retry-NACHA-HELD0001").Should().BeFalse();
    }
}

public class SponsorBankAccountsPageTests : BillingPageTestBase
{
    private const string Group = "G1";

    public SponsorBankAccountsPageTests()
    {
        Api.On("GET", "/api/v1/sponsors/G1/bank-account", HttpStatusCode.OK, BillingApiShapes.BankAccountView(withLeak: true))
            .On("GET", "/api/v1/sponsors/G1/bank-account-changes", HttpStatusCode.OK, BillingApiShapes.ChangeHistory);
    }

    private IRenderedComponent<SponsorBankAccounts> Render()
    {
        var cut = RenderAt<SponsorBankAccounts>($"/premium-billing/sponsor-bank-accounts?group={Group}");
        cut.WaitForAssertion(() => Has(cut, "sba-active").Should().BeTrue());
        return cut;
    }

    [Fact]
    public void Reader_SeesMaskedAccounts_Only()
    {
        SignIn("fa-0", "billing:read");

        var cut = Render();

        Api.CallsTo("GET", "/api/v1/sponsors/G1/bank-account").Should().ContainSingle();
        cut.Find("[data-testid=sba-active] [data-testid=masked-account]").TextContent.Should().Be("••••6789");
        cut.Find("[data-testid=sba-active] [data-testid=masked-routing]").TextContent.Should().Be("••••0019");
        cut.Find("[data-testid=sba-pending] [data-testid=masked-account]").TextContent.Should().Be("••••4321");
        cut.Find("[data-testid=sba-history]").TextContent.Should().Contain("approver-1").And.Contain("verified");
        // The stubbed response also carried full numbers; none reaches the page.
        cut.Markup.Should().NotContain(BillingApiShapes.FullAccountNumber);
        cut.Markup.Should().NotContain(BillingApiShapes.FullRoutingNumber);
        Has(cut, "sba-approve").Should().BeFalse();
        Has(cut, "sba-reject").Should().BeFalse();
        Has(cut, "sba-propose-form").Should().BeFalse();
        Has(cut, "sba-cancel").Should().BeFalse();
    }

    [Fact]
    public void Approver_WhoDidNotPropose_CanApproveAndReject()
    {
        SignIn("approver-1", "billing:read", "payments:approve");

        var cut = Render();

        Has(cut, "sba-approve").Should().BeTrue();
        Has(cut, "sba-reject").Should().BeTrue();
        Has(cut, "sba-own-proposal").Should().BeFalse();
        Has(cut, "sba-propose-form").Should().BeFalse("FinanceApprover holds no billing:run");
    }

    [Fact]
    public void Proposer_WithApprove_IsNotOfferedApproval()
    {
        SignIn("proposer-1", "billing:read", "billing:run", "payments:approve");

        var cut = Render();

        Has(cut, "sba-approve").Should().BeFalse();
        Has(cut, "sba-reject").Should().BeFalse();
        cut.Find("[data-testid=sba-own-proposal]").TextContent.Should().Contain("separation of duties");
        Has(cut, "sba-cancel").Should().BeTrue("the proposer side can withdraw");
    }

    [Fact]
    public void Approve_PostsTheDecision_AndReloads()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve", HttpStatusCode.OK, BillingApiShapes.ChangeApproved);
        var cut = Render();

        Type(cut, "sba-decision-reason", "Confirmed by phone");
        Click(cut, "sba-approve");

        cut.WaitForAssertion(() => Has(cut, "sba-notice").Should().BeTrue());
        Json(Api.CallsTo("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve").Single())
            .GetProperty("reason").GetString().Should().Be("Confirmed by phone");
        Api.CallsTo("GET", "/api/v1/sponsors/G1/bank-account").Should().HaveCount(2);
    }

    [Fact]
    public void Approve_SeparationOfDuties_IsShownClearly()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve", HttpStatusCode.Forbidden, BillingApiShapes.SponsorSeparationOfDuties);
        var cut = Render();

        Click(cut, "sba-approve");

        cut.WaitForAssertion(() => Has(cut, "billing-error-sod").Should().BeTrue());
        cut.Find("[data-testid=billing-error-sod]").TextContent.Should().Contain("Separation of duties")
            .And.Contain("proposed a bank-account change cannot approve");
    }

    [Fact]
    public void Approve_Stale_IsShownClearly()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve", HttpStatusCode.Conflict, BillingApiShapes.Stale);
        var cut = Render();

        Click(cut, "sba-approve");

        cut.WaitForAssertion(() => Has(cut, "billing-error-stale").Should().BeTrue());
        cut.Find("[data-testid=billing-error-stale]").TextContent.Should().Contain("Reload").And.Contain("active account changed");
    }

    [Fact]
    public void Reject_PostsToReject()
    {
        SignIn("approver-1", "billing:read", "payments:approve");
        Api.On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/reject", HttpStatusCode.OK, BillingApiShapes.ChangeApproved);
        var cut = Render();

        Click(cut, "sba-reject");

        cut.WaitForAssertion(() => Api.CallsTo("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/reject").Should().ContainSingle());
    }

    [Theory]
    [InlineData("billing:run")]
    [InlineData("enrollment:process")]
    public void Proposer_ProposesAChange_AndTheNumbersAreNotKept(string proposePermission)
    {
        SignIn("fin-1", "billing:read", proposePermission);
        Api.On("POST", "/api/v1/sponsors/G1/bank-account-changes", HttpStatusCode.Accepted, BillingApiShapes.ChangeApproved);
        var cut = Render();

        Type(cut, "sba-holder", "Acme Corp Payroll");
        Type(cut, "sba-routing", "011000015");
        Type(cut, "sba-account", "998877665544");
        Click(cut, "sba-propose");

        cut.WaitForAssertion(() => Has(cut, "sba-notice").Should().BeTrue());
        var body = Json(Api.CallsTo("POST", "/api/v1/sponsors/G1/bank-account-changes").Single());
        body.GetProperty("routingNumber").GetString().Should().Be("011000015");
        body.GetProperty("accountNumber").GetString().Should().Be("998877665544");
        body.GetProperty("accountHolderName").GetString().Should().Be("Acme Corp Payroll");
        body.GetProperty("preferredMethod").GetString().Should().Be("Nacha");
        body.GetProperty("accountType").GetString().Should().Be("Checking");
        body.GetProperty("eftEnabled").GetBoolean().Should().BeTrue();
        cut.Markup.Should().NotContain("998877665544");
        cut.Markup.Should().NotContain("011000015");
    }

    [Fact]
    public void Proposer_KeepingTheNumbers_SendsNone()
    {
        SignIn("fin-1", "billing:read", "billing:run");
        Api.On("POST", "/api/v1/sponsors/G1/bank-account-changes", HttpStatusCode.Accepted, BillingApiShapes.ChangeApproved);
        var cut = Render();

        Click(cut, "sba-propose");

        cut.WaitForAssertion(() => Api.CallsTo("POST", "/api/v1/sponsors/G1/bank-account-changes").Should().ContainSingle());
        var body = Json(Api.CallsTo("POST", "/api/v1/sponsors/G1/bank-account-changes").Single());
        body.TryGetProperty("routingNumber", out _).Should().BeFalse();
        body.TryGetProperty("accountNumber", out _).Should().BeFalse();
    }

    [Fact]
    public void NoApprovedAccount_IsFlagged()
    {
        SignIn("fin-1", "billing:read");
        Api.On("GET", "/api/v1/sponsors/G5/bank-account", HttpStatusCode.OK, BillingApiShapes.BankAccountNone)
            .On("GET", "/api/v1/sponsors/G5/bank-account-changes", HttpStatusCode.OK, "[]");

        var cut = RenderAt<SponsorBankAccounts>("/premium-billing/sponsor-bank-accounts?group=G5");

        cut.WaitForAssertion(() => cut.Find("[data-testid=sba-no-active]").TextContent.Should().Contain("needs attention"));
    }

    [Fact]
    public void UnknownGroup_IsReported()
    {
        SignIn("fin-1", "billing:read");
        Api.On("GET", "/api/v1/sponsors/NOPE/bank-account", HttpStatusCode.NotFound, """{"error":"Sponsor with group number 'NOPE' not found"}""");

        var cut = RenderComponent<SponsorBankAccounts>();
        Type(cut, "sba-group", "NOPE");
        Click(cut, "sba-load");

        cut.WaitForAssertion(() => Has(cut, "sba-not-found").Should().BeTrue());
    }
}

/// <summary>Who may open each premium billing page.</summary>
public class PremiumBillingPageGateTests : BillingPageTestBase
{
    [Fact]
    public void WithoutBillingRead_RunsInvoicesAndDelinquencyAreDenied_AndNothingIsCalled()
    {
        SignIn("x-1", "payments:read");

        foreach (var cut in new IRenderedFragment[]
                 {
                     RenderComponent<PremiumBilling>(), RenderComponent<PremiumBillingInvoices>(), RenderComponent<PremiumBillingDelinquency>()
                 })
        {
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("don't have access"));
        }
        Api.Calls.Should().BeEmpty();
    }

    [Fact]
    public void WithNeitherBillingNorPaymentsRead_EftAndBankAccountsAreDenied()
    {
        SignIn("x-1", "claims:read");

        var eft = RenderComponent<PremiumBillingEft>();
        var bank = RenderComponent<SponsorBankAccounts>();

        eft.WaitForAssertion(() => eft.Markup.Should().Contain("don't have access"));
        bank.WaitForAssertion(() => bank.Markup.Should().Contain("don't have access"));
        Api.Calls.Should().BeEmpty();
    }
}
