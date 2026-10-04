using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>
/// The portal's premium billing client against premium-billing-service's real
/// routes and shapes (api/v1/billing-runs, premium-invoices, eft). Before, it
/// called routes that do not exist (billing-runs/{id}/mark-paid,
/// billing-runs/{id}/invoice, PUT premium-invoices/{id}), read invoices as
/// "rates", and posted a body the billing-run endpoint does not take.
/// </summary>
public class PremiumBillingServiceTests
{
    private const string Base = "http://premium-billing-service/api";
    private readonly BillingApiStub _api = new();

    private PremiumBillingService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:BillingService"] = Base })
            .Build();
        return new PremiumBillingService(new HttpClient(_api), config, NullLogger<PremiumBillingService>.Instance);
    }

    private static JsonElement Body(BillingApiStub.Call call) => JsonDocument.Parse(call.Body!).RootElement;

    // ── Billing runs ──

    [Fact]
    public async Task GetBillingRuns_ReadsTheServiceShape()
    {
        _api.On("GET", "/api/v1/billing-runs", HttpStatusCode.OK, $"[{BillingApiShapes.RunPending},{BillingApiShapes.RunCompleted}]");

        var runs = await CreateService().GetBillingRunsAsync();

        runs.Should().HaveCount(2);
        var done = runs[1];
        done.BillingRunNumber.Should().Be("BR-2026-02-001");
        done.Status.Should().Be("Completed");
        done.Criteria.GroupNumbers.Should().Equal("G1", "G2");
        done.InvoiceIds.Should().Equal("inv-1", "inv-2");
        done.TotalPremiumAmount.Should().Be(12500.50m);
        done.ExecutedBy.Should().Be("maker-2");
        done.Errors.Should().ContainSingle();
        done.Warnings.Should().ContainSingle().Which.Should().Contain("no approved bank account");
    }

    [Fact]
    public async Task GetBillingRuns_DateRange_IsSentAsFromAndTo()
    {
        _api.On("GET", "/api/v1/billing-runs", HttpStatusCode.OK, "[]");

        await CreateService().GetBillingRunsAsync(new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));

        _api.Calls.Single().PathAndQuery.Should().Be("/api/v1/billing-runs?from=2026-01-01&to=2026-03-31");
    }

    [Fact]
    public async Task CreateBillingRun_PostsTheBillingRunBody()
    {
        _api.On("POST", "/api/v1/billing-runs", HttpStatusCode.Created, BillingApiShapes.RunPending);

        var run = await CreateService().CreateBillingRunAsync(new CreateBillingRunRequest
        {
            BillingPeriod = new DateTime(2026, 3, 1),
            Description = "March",
            Criteria = new BillingRunCriteria { GroupNumbers = { "G1" } }
        });

        run.Id.Should().Be("run-1");
        var body = Body(_api.CallsTo("POST", "/api/v1/billing-runs").Single());
        body.GetProperty("billingPeriod").GetString().Should().StartWith("2026-03-01");
        body.GetProperty("criteria").GetProperty("groupNumbers").EnumerateArray().Select(e => e.GetString()).Should().Equal("G1");
        body.GetProperty("description").GetString().Should().Be("March");
        body.TryGetProperty("createdBy", out _).Should().BeFalse("the creator comes from the token");
        body.TryGetProperty("sponsorId", out _).Should().BeFalse("the old invoice body is gone");
    }

    [Fact]
    public async Task ExecuteAndCancel_UseTheRunActionRoutes()
    {
        _api.On("POST", "/api/v1/billing-runs/run-1/execute", HttpStatusCode.OK, BillingApiShapes.RunCompleted)
            .On("POST", "/api/v1/billing-runs/run-1/cancel", HttpStatusCode.NoContent);
        var sut = CreateService();

        var executed = await sut.ExecuteBillingRunAsync("run-1");
        await sut.CancelBillingRunAsync("run-1");

        executed.Status.Should().Be("Completed");
        _api.Calls.Select(c => $"{c.Method} {c.Path}").Should().Equal(
            "POST /api/v1/billing-runs/run-1/execute", "POST /api/v1/billing-runs/run-1/cancel");
    }

    [Fact]
    public async Task ExecuteRefused_CarriesTheServiceReason()
    {
        _api.On("POST", "/api/v1/billing-runs/run-1/execute", HttpStatusCode.BadRequest,
            """{"error":"Billing run run-1 is Completed and cannot be executed"}""");

        var ex = await Assert.ThrowsAsync<BillingApiException>(() => CreateService().ExecuteBillingRunAsync("run-1"));

        ex.StatusCode.Should().Be(400);
        ex.UserMessage.Should().Contain("cannot be executed");
    }

    [Fact]
    public async Task GetBillingRun_NotFound_ReturnsNull()
    {
        _api.On("GET", "/api/v1/billing-runs/nope", HttpStatusCode.NotFound, """{"error":"Billing run nope not found"}""");

        (await CreateService().GetBillingRunAsync("nope")).Should().BeNull();
    }

    // ── Invoices ──

    [Fact]
    public async Task SearchInvoices_SendsTheServiceFilters_AndReadsInvoices()
    {
        _api.On("GET", "/api/v1/premium-invoices", HttpStatusCode.OK, $"[{BillingApiShapes.Invoice(suspension: BillingApiShapes.FailedSuspension)}]");

        var invoices = await CreateService().SearchInvoicesAsync("G 1", "Delinquent", page: 2, pageSize: 25);

        _api.Calls.Single().PathAndQuery.Should().Be("/api/v1/premium-invoices?page=2&pageSize=25&groupNumber=G%201&status=Delinquent");
        var inv = invoices.Single();
        inv.InvoiceNumber.Should().Be("INV-G1-2026-02");
        inv.BalanceDue.Should().Be(450m);
        inv.LineItems.Single().TotalPremium.Should().Be(500m);
        inv.Adjustments.Single().Type.Should().Be("Credit");
        inv.SponsorSuspension!.State.Should().Be("Failed");
        inv.SponsorSuspension.LastStatusCode.Should().Be(503);
    }

    [Fact]
    public async Task RecordPayment_PostsAmountDateMethodAndReference()
    {
        _api.On("POST", "/api/v1/premium-invoices/inv-1/payments", HttpStatusCode.OK, BillingApiShapes.Invoice(status: "Paid"));

        var updated = await CreateService().RecordPaymentAsync("inv-1", new RecordPremiumPaymentRequest
        {
            Amount = 450m, PaymentDate = new DateTime(2026, 2, 20), PaymentMethod = "Check", ReferenceNumber = "CHK-100"
        });

        updated.Status.Should().Be("Paid");
        var body = Body(_api.Calls.Single());
        body.GetProperty("amount").GetDecimal().Should().Be(450m);
        body.GetProperty("paymentDate").GetString().Should().StartWith("2026-02-20");
        body.GetProperty("paymentMethod").GetString().Should().Be("Check");
        body.GetProperty("referenceNumber").GetString().Should().Be("CHK-100");
    }

    [Fact]
    public async Task VoidInvoice_PostsTheReason()
    {
        _api.On("POST", "/api/v1/premium-invoices/inv-1/void", HttpStatusCode.OK, BillingApiShapes.Invoice(status: "Voided"));

        await CreateService().VoidInvoiceAsync("inv-1", "Duplicate invoice");

        Body(_api.Calls.Single()).GetProperty("reason").GetString().Should().Be("Duplicate invoice");
    }

    [Fact]
    public async Task VoidWithoutFinanceWrite_Is403_NotSeparationOfDuties()
    {
        _api.On("POST", "/api/v1/premium-invoices/inv-1/void", HttpStatusCode.Forbidden,
            """{"title":"Forbidden","status":403,"detail":"Requires permission finance:write"}""");

        var ex = await Assert.ThrowsAsync<BillingApiException>(() => CreateService().VoidInvoiceAsync("inv-1", "x"));

        ex.IsSeparationOfDuties.Should().BeFalse();
        ex.UserMessage.Should().Contain("permission").And.Contain("finance:write");
    }

    [Fact]
    public async Task ProcessDelinquencies_200_ReturnsTheResult()
    {
        _api.On("POST", "/api/v1/premium-invoices/process-delinquencies", HttpStatusCode.OK, BillingApiShapes.DelinquencyOk);

        var result = await CreateService().ProcessDelinquenciesAsync();

        result.DelinquentCount.Should().Be(2);
        result.SuspensionsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessDelinquencies_502_ReturnsTheFailuresInsteadOfThrowing()
    {
        // 502 means "invoices marked delinquent, but some sponsors were not
        // suspended": the portal must show which, not a generic error.
        _api.On("POST", "/api/v1/premium-invoices/process-delinquencies", HttpStatusCode.BadGateway, BillingApiShapes.DelinquencyFailed);

        var result = await CreateService().ProcessDelinquenciesAsync();

        result.SuspensionsFailed.Should().BeTrue();
        result.DelinquentCount.Should().Be(3);
        result.SuspensionRetries.Should().Be(1);
        var failure = result.SuspensionFailures.Single();
        failure.GroupNumber.Should().Be("G7");
        failure.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task AgingReport_ReadsBuckets()
    {
        _api.On("GET", "/api/v1/premium-invoices/aging-report", HttpStatusCode.OK, BillingApiShapes.Aging);

        var aging = await CreateService().GetAgingReportAsync();

        aging.TotalOutstanding.Should().Be(1450m);
        aging.ThirtyDayCount.Should().Be(1);
    }

    // ── EFT ──

    [Fact]
    public async Task DraftsForInvoices_ReadsEachInvoicesDrafts()
    {
        _api.On("GET", "/api/v1/eft/drafts/invoice/inv-1", HttpStatusCode.OK, $"[{BillingApiShapes.Draft("inv-1")}]")
            .On("GET", "/api/v1/eft/drafts/invoice/inv-2", HttpStatusCode.OK, $"[{BillingApiShapes.Draft("inv-2", "Pending")}]");

        var drafts = await CreateService().GetDraftsForInvoicesAsync(new[] { "inv-1", "inv-2", "inv-1" });

        drafts.Select(d => d.InvoiceId).Should().BeEquivalentTo("inv-1", "inv-2");
        _api.Calls.Should().HaveCount(2);
        drafts.First().AccountNumberLast4.Should().Be("6789");
    }

    [Fact]
    public async Task BatchRelease_PostsTheRunAndOmitsAnUnsetMethod()
    {
        _api.On("POST", "/api/v1/eft/drafts/batch", HttpStatusCode.OK, BillingApiShapes.BatchWithAttention);

        var result = await CreateService().InitiateBatchDraftsAsync(new InitiateBatchEftRequest { BillingRunId = "run-2" });

        var body = Body(_api.Calls.Single());
        body.GetProperty("billingRunId").GetString().Should().Be("run-2");
        body.TryGetProperty("method", out _).Should().BeFalse("null means the sponsor's preferred method");
        body.TryGetProperty("initiatedBy", out _).Should().BeFalse("the initiator comes from the token");
        result.NeedsAttention.Single().GroupNumber.Should().Be("G3");
        result.Errors.Should().Be(1);
    }

    [Fact]
    public async Task SingleRelease_SendsTheMethodAsItsName()
    {
        _api.On("POST", "/api/v1/eft/drafts", HttpStatusCode.Created, BillingApiShapes.Draft("inv-1", "Pending"));

        await CreateService().InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1", Method = "StripeAch" });

        var body = Body(_api.Calls.Single());
        body.GetProperty("invoiceId").GetString().Should().Be("inv-1");
        body.GetProperty("method").GetString().Should().Be("StripeAch");
    }

    [Fact]
    public async Task Release_SeparationOfDuties_IsRecognised()
    {
        _api.On("POST", "/api/v1/eft/drafts/batch", HttpStatusCode.Forbidden, BillingApiShapes.SeparationOfDuties);

        var ex = await Assert.ThrowsAsync<BillingApiException>(
            () => CreateService().InitiateBatchDraftsAsync(new InitiateBatchEftRequest { BillingRunId = "run-2" }));

        ex.IsSeparationOfDuties.Should().BeTrue();
        ex.UserMessage.Should().Contain("Separation of duties").And.Contain("executed billing run BR-2026-02-001");
    }

    [Fact]
    public async Task GenerateNacha_ReadsTheResultAndNeedsAttention()
    {
        _api.On("POST", "/api/v1/eft/nacha/generate", HttpStatusCode.OK, BillingApiShapes.NachaResult);

        var file = await CreateService().GenerateNachaFileAsync();

        file.FileReference.Should().Be("NACHA-20260216");
        file.EntryCount.Should().Be(1);
        file.TransmissionStatus.Should().Be("Transmitted");
        file.Receipt!.Sha256.Should().HaveLength(64);
        file.Entries.Single().AccountNumberLast4.Should().Be("6789");
        file.NeedsAttention.Single().Reason.Should().Contain("refused");
        typeof(NachaFileResult).GetProperty("FileContent").Should().BeNull("the portal never receives a NACHA file");
    }

    [Fact]
    public async Task HeldFiles_AndRetry_UseTheServiceEndpoints()
    {
        _api.On("GET", "/api/v1/eft/nacha/held", HttpStatusCode.OK, BillingApiShapes.HeldFiles);
        _api.On("POST", "/api/v1/eft/nacha/held/NACHA-HELD0001/retry", HttpStatusCode.OK, BillingApiShapes.NachaResult);

        var held = await CreateService().GetHeldNachaFilesAsync();
        var retried = await CreateService().RetryNachaTransmissionAsync("NACHA-HELD0001");

        held.Single().ReleasedBy.Should().Be("approver-1");
        retried.TransmissionStatus.Should().Be("Transmitted");
    }

    [Fact]
    public async Task Unreachable_ThrowsServiceUnavailable()
    {
        _api.ThrowConnectionError = true;

        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => CreateService().GetBillingRunsAsync());

        ex.ServiceName.Should().Be("Premium Billing Service");
        ex.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public void Client_HasNoRoutesThatDoNotExist()
    {
        // The old client's routes (and its rate/cycle methods) are gone.
        var methods = typeof(IPremiumBillingService).GetMethods().Select(m => m.Name).ToList();
        methods.Should().NotContain(new[]
        {
            "MarkCycleAsPaidAsync", "DownloadInvoiceAsync", "UpdatePremiumRateAsync", "GetPremiumRatesAsync",
            "GenerateInvoiceAsync", "GetBillingCyclesAsync"
        });
    }
}

/// <summary>sponsor-service's sponsor bank-account endpoints, as the portal calls them.</summary>
public class SponsorBankAccountServiceTests
{
    private readonly BillingApiStub _api = new();

    private SponsorBankAccountService CreateService()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Services:SponsorService"] = "http://sponsor-service/api/v1" })
            .Build();
        return new SponsorBankAccountService(new HttpClient(_api), config, NullLogger<SponsorBankAccountService>.Instance);
    }

    [Fact]
    public async Task GetBankAccount_ReadsTheMaskedView()
    {
        _api.On("GET", "/api/v1/sponsors/G1/bank-account", HttpStatusCode.OK, BillingApiShapes.BankAccountView());

        var view = await CreateService().GetBankAccountAsync("G1");

        view!.Active!.AccountNumberLast4.Should().Be("6789");
        view.Active.PreferredMethod.Should().Be("Nacha");
        view.Pending!.Id.Should().Be("chg-2");
        view.Pending.RequestedBy.Should().Be("proposer-1");
        view.Pending.Proposed!.AccountType.Should().Be("Savings");
    }

    [Fact]
    public void MaskedModel_HasNoFullNumberProperties()
    {
        // Even a response carrying full numbers cannot reach a page.
        var names = typeof(SponsorBankAccountMasked).GetProperties().Select(p => p.Name);
        names.Should().NotContain(new[] { "RoutingNumber", "AccountNumber" });
        typeof(EftDraft).GetProperties().Select(p => p.Name).Should().NotContain(new[] { "RoutingNumber", "AccountNumber" });
    }

    [Fact]
    public async Task UnknownSponsor_ReturnsNull()
    {
        _api.On("GET", "/api/v1/sponsors/G404/bank-account", HttpStatusCode.NotFound, """{"error":"Sponsor with group number 'G404' not found"}""");

        (await CreateService().GetBankAccountAsync("G404")).Should().BeNull();
    }

    [Fact]
    public async Task Propose_PostsTheAccountAndEnrollment()
    {
        _api.On("POST", "/api/v1/sponsors/G1/bank-account-changes", HttpStatusCode.Accepted, BillingApiShapes.ChangeApproved);

        await CreateService().ProposeChangeAsync("G1", new ProposeSponsorBankAccountRequest
        {
            EftEnabled = true, PreferredMethod = "Nacha", AccountType = "Savings", AccountHolderName = "Acme",
            RoutingNumber = "011000015", AccountNumber = "123456789"
        });

        var body = JsonDocument.Parse(_api.Calls.Single().Body!).RootElement;
        body.GetProperty("eftEnabled").GetBoolean().Should().BeTrue();
        body.GetProperty("preferredMethod").GetString().Should().Be("Nacha");
        body.GetProperty("accountType").GetString().Should().Be("Savings");
        body.GetProperty("routingNumber").GetString().Should().Be("011000015");
        body.GetProperty("accountNumber").GetString().Should().Be("123456789");
    }

    [Fact]
    public async Task ApproveRejectCancel_UseTheChangeRoutes_WithReason()
    {
        _api.On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve", HttpStatusCode.OK, BillingApiShapes.ChangeApproved)
            .On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/reject", HttpStatusCode.OK, BillingApiShapes.ChangeApproved)
            .On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/cancel", HttpStatusCode.OK, BillingApiShapes.ChangeApproved);
        var sut = CreateService();

        await sut.ApproveChangeAsync("G1", "chg-2", "called the sponsor");
        await sut.RejectChangeAsync("G1", "chg-2", null);
        await sut.CancelChangeAsync("G1", "chg-2", " ");

        _api.Calls.Select(c => c.Path).Should().Equal(
            "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve",
            "/api/v1/sponsors/G1/bank-account-changes/chg-2/reject",
            "/api/v1/sponsors/G1/bank-account-changes/chg-2/cancel");
        JsonDocument.Parse(_api.Calls[0].Body!).RootElement.GetProperty("reason").GetString().Should().Be("called the sponsor");
        _api.Calls[1].Body.Should().Be("{}");
    }

    [Fact]
    public async Task Approve_ByTheProposer_IsSeparationOfDuties()
    {
        _api.On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve", HttpStatusCode.Forbidden, BillingApiShapes.SponsorSeparationOfDuties);

        var ex = await Assert.ThrowsAsync<BillingApiException>(() => CreateService().ApproveChangeAsync("G1", "chg-2", null));

        ex.IsSeparationOfDuties.Should().BeTrue();
        ex.UserMessage.Should().Contain("different user");
    }

    [Fact]
    public async Task Approve_Stale_Is409()
    {
        _api.On("POST", "/api/v1/sponsors/G1/bank-account-changes/chg-2/approve", HttpStatusCode.Conflict, BillingApiShapes.Stale);

        var ex = await Assert.ThrowsAsync<BillingApiException>(() => CreateService().ApproveChangeAsync("G1", "chg-2", null));

        ex.IsStale.Should().BeTrue();
        ex.UserMessage.Should().Contain("Reload").And.Contain("active account changed");
    }

    [Fact]
    public async Task InvalidAccount_ShowsTheValidationErrors()
    {
        _api.On("POST", "/api/v1/sponsors/G1/bank-account-changes", HttpStatusCode.BadRequest,
            """{"error":"The bank account is not valid","errors":["The routing number is not a valid ABA routing number."]}""");

        var ex = await Assert.ThrowsAsync<BillingApiException>(() =>
            CreateService().ProposeChangeAsync("G1", new ProposeSponsorBankAccountRequest { RoutingNumber = "1", AccountNumber = "2" }));

        ex.UserMessage.Should().Contain("not valid").And.Contain("valid ABA routing number");
    }
}
