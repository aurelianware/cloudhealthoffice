using System.Net;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using static PremiumBillingService.Tests.Services.PremiumBillingServiceTests;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// Billing a sponsor on its contract's billing terms rather than the flat
/// billing information on the sponsor record
/// (docs/architecture/plan-configuration.md §9b, step 3d, §12.10).
///
/// <para>
/// The behaviour these pin is the fallback and its one exception. A group with
/// no contract bills exactly as it does today, which is every group until a
/// contract is entered; a group with one bills on the contract. A contract that
/// could not be read is neither: that sponsor is left unbilled with a warning
/// on the run, because billing them on the sponsor record would quietly bill
/// last year's due day under this year's agreement and nothing downstream would
/// ever show it.
/// </para>
/// </summary>
public class ContractBillingTermsTests
{
    private const string Tenant = "tenant-1";

    private readonly Mock<IBillingRunRepository> _runs = new();
    private readonly Mock<IPremiumInvoiceRepository> _invoices = new();
    private readonly Mock<IHttpClientFactory> _http = new();
    private readonly Mock<ICurrentActor> _actor = new();
    private readonly ListLogger<PremiumBillingService.Services.PremiumBillingService> _log = new();
    private readonly List<HttpRequestMessage> _sponsorRequests = new();
    private readonly List<PremiumInvoice> _created = new();

    public ContractBillingTermsTests()
    {
        _actor.SetupGet(a => a.UserId).Returns("finance-user");
        _actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        _runs.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun r) => r);
        _invoices.Setup(r => r.CreateAsync(It.IsAny<PremiumInvoice>()))
            .Callback<PremiumInvoice>(i => _created.Add(i))
            .ReturnsAsync((PremiumInvoice i) => { i.RecalculateTotals(); return i; });
        _invoices.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>())).ReturnsAsync((PremiumInvoice i) => i);
        _invoices.Setup(r => r.GetByGroupNumberAsync(It.IsAny<string>()))
            .ReturnsAsync(Array.Empty<PremiumInvoice>());
    }

    private PremiumBillingService.Services.PremiumBillingService Service() => new(
        _runs.Object, _invoices.Object,
        new SponsorServiceClient(_http.Object, NullLogger<SponsorServiceClient>.Instance),
        new CoverageServiceClient(_http.Object, NullLogger<CoverageServiceClient>.Instance),
        _actor.Object, _log);

    /// <summary>
    /// One sponsor on the record, and whatever the contract route answers. The
    /// handler answers the contract route itself, so the default 404 is out of
    /// the way.
    /// </summary>
    private void SponsorService(SponsorDto onRecord, Func<HttpRequestMessage, HttpResponseMessage> contract)
        => _http.Setup(f => f.CreateClient(SponsorServiceClient.HttpClientName)).Returns(() =>
            new HttpClient(new BillingMockHttpMessageHandler(req =>
            {
                _sponsorRequests.Add(req);
                return req.RequestUri!.AbsolutePath.EndsWith("/billing-terms", StringComparison.Ordinal)
                    ? contract(req)
                    : Json(SponsorPage(new[] { onRecord }));
            }, answersContractReads: true))
            { BaseAddress = new Uri("http://sponsor-service") });

    private void Coverage()
        => _http.Setup(f => f.CreateClient(CoverageServiceClient.HttpClientName)).Returns(() =>
            new HttpClient(new BillingMockHttpMessageHandler(_ => Json(CoveragePage(new[]
            {
                new CoverageDto
                {
                    CoverageId = "cov-1", MemberId = "m-1", GroupNumber = "GRP001",
                    EffectiveDate = Period.AddYears(-1), MonthlyPremium = 500m, EmployerContribution = 100m,
                },
            }))))
            { BaseAddress = new Uri("http://coverage-service") });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage NotFound(string reason) => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent($"{{\"reason\":\"{reason}\",\"error\":\"nothing in force\"}}"),
    };

    /// <summary>
    /// The month being billed: next month, so the production rule that pushes
    /// a due date already past out by 30 days does not hide which day the
    /// contract asked for. Computed rather than written down, because a fixed
    /// date would start failing the moment it fell into the past.
    /// </summary>
    private static readonly DateTime Period =
        new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

    private static HttpResponseMessage Contract(int dueDay, int graceDays, string? paymentMethod = "ACH") =>
        Json($$"""
            {
              "asOf": "{{Period:yyyy-MM-dd}}",
              "contractPeriodStart": "{{Period:yyyy-MM-dd}}T00:00:00Z",
              "contractStatus": "Active",
              "billing": {
                "method": "ListBill", "frequency": "Monthly",
                "dueDay": {{dueDay}}, "gracePeriodDays": {{graceDays}},
                "billingAccountNumberLast4": "4321",
                "paymentMethod": {{(paymentMethod is null ? "null" : $"\"{paymentMethod}\"")}}
              }
            }
            """);

    private BillingRun PendingRun()
    {
        var run = new BillingRun
        {
            Id = "br-1", TenantId = Tenant, BillingRunNumber = "BR-TEST",
            Status = BillingRunStatus.Pending, BillingPeriod = Period,
            Criteria = new BillingRunCriteria(),
        };
        _runs.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(run);
        return run;
    }

    private static SponsorDto OnRecord(int billingDay = 1, int graceDays = 30, string? paymentMethod = "Check") => new()
    {
        GroupNumber = "GRP001", EmployerName = "Acme", BillingDay = billingDay,
        GracePeriodDays = graceDays, PaymentMethod = paymentMethod,
    };

    [Fact]
    public async Task A_contract_in_force_decides_the_due_day_and_the_grace_period()
    {
        PendingRun();
        Coverage();
        SponsorService(OnRecord(billingDay: 1, graceDays: 30), _ => Contract(dueDay: 15, graceDays: 45));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        var invoice = _created.Should().ContainSingle().Subject;
        invoice.GracePeriodDays.Should().Be(45);
        invoice.DueDate.Day.Should().Be(15);
        invoice.GracePeriodExpires.Should().Be(invoice.DueDate.AddDays(45));
    }

    [Fact]
    public async Task The_contract_is_asked_about_the_period_being_billed_not_today()
    {
        // Terms are per contract year, so the only date that can be asked
        // about is the one being billed. Asking about "now" would bill March on
        // next January's renewal once the renewal is entered.
        PendingRun();
        Coverage();
        SponsorService(OnRecord(), _ => Contract(dueDay: 10, graceDays: 30));

        await Service().ExecuteBillingRunAsync("br-1");

        _sponsorRequests.Should().ContainSingle(r => r.RequestUri!.AbsolutePath.EndsWith("/billing-terms", StringComparison.Ordinal))
            .Which.RequestUri!.Query.Should().Contain($"asOf={Period:yyyy-MM-dd}");
    }

    [Fact]
    public async Task The_contract_call_names_its_tenant()
    {
        PendingRun();
        Coverage();
        SponsorService(OnRecord(), _ => Contract(dueDay: 10, graceDays: 30));

        await Service().ExecuteBillingRunAsync("br-1");

        _sponsorRequests.Should().OnlyContain(r => r.Headers.GetValues("X-Tenant-ID").Single() == Tenant);
    }

    [Fact]
    public async Task A_group_with_no_contract_bills_exactly_as_it_does_today()
    {
        // Every group, until a contract is entered. The fallback is the point:
        // this change must be invisible to a tenant that has not used it.
        PendingRun();
        Coverage();
        SponsorService(OnRecord(billingDay: 7, graceDays: 21), _ => NotFound("NoContractTerm"));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        var invoice = _created.Should().ContainSingle().Subject;
        invoice.GracePeriodDays.Should().Be(21);
        invoice.DueDate.Day.Should().Be(7);
    }

    [Fact]
    public async Task A_contract_whose_billing_was_left_blank_falls_back_the_same_way()
    {
        // The sponsor record is where billing lived before the contract
        // existed, so it is not stale on a contract's account. It is still
        // worth a line in the log: somebody agreed a contract and left its
        // billing empty.
        PendingRun();
        Coverage();
        SponsorService(OnRecord(billingDay: 7, graceDays: 21), _ => NotFound("BillingNotEntered"));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        _created.Should().ContainSingle().Which.DueDate.Day.Should().Be(7);
    }

    [Fact]
    public async Task A_contract_that_could_not_be_read_leaves_that_sponsor_unbilled()
    {
        // The distinction the whole design turns on: "could not look" is not
        // "there is none". Billing them on the record would be billing an
        // agreement nobody could read.
        PendingRun();
        Coverage();
        SponsorService(OnRecord(), _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        result.TotalInvoices.Should().Be(0);
        result.Warnings.Should().ContainSingle(w => w.Contains("GRP001") && w.Contains("503"));
        _invoices.Verify(r => r.CreateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
    }

    [Fact]
    public async Task A_contract_refused_to_this_caller_leaves_that_sponsor_unbilled_too()
    {
        // A 403 is a permission the deployment has not granted, not an answer
        // about the contract.
        PendingRun();
        Coverage();
        SponsorService(OnRecord(), _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.TotalInvoices.Should().Be(0);
        result.Warnings.Should().ContainSingle(w => w.Contains("GRP001") && w.Contains("403"));
    }

    [Fact]
    public async Task An_answer_with_no_billing_block_is_refused_rather_than_read_as_no_contract()
    {
        // A 200 with nothing in it is a wire mismatch, which must not quietly
        // become the fallback: the fallback is for a contract that answered.
        PendingRun();
        Coverage();
        SponsorService(OnRecord(), _ => Json("""{"asOf":"2026-03-01","contractPeriodStart":"2026-01-01T00:00:00Z"}"""));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.TotalInvoices.Should().Be(0);
        result.Warnings.Should().ContainSingle(w => w.Contains("GRP001"));
    }

    [Fact]
    public async Task A_payment_method_the_contract_leaves_blank_keeps_the_records_own()
    {
        // A blank is an operational detail nobody agreed, not a method of
        // "none" — and premium billing reads the method to decide whether a
        // group is drafted.
        PendingRun();
        Coverage();
        SponsorService(OnRecord(paymentMethod: "ACH"), _ => Contract(dueDay: 5, graceDays: 30, paymentMethod: null));

        await Service().ExecuteBillingRunAsync("br-1");

        // The due day still came from the contract, so the terms were applied.
        _created.Should().ContainSingle().Which.DueDate.Day.Should().Be(5);
    }

    [Fact]
    public async Task One_sponsors_unreadable_contract_does_not_stop_the_others_being_billed()
    {
        PendingRun();
        Coverage();
        _http.Setup(f => f.CreateClient(SponsorServiceClient.HttpClientName)).Returns(() =>
            new HttpClient(new BillingMockHttpMessageHandler(req =>
            {
                if (!req.RequestUri!.AbsolutePath.EndsWith("/billing-terms", StringComparison.Ordinal))
                    return Json(SponsorPage(new[]
                    {
                        new SponsorDto { GroupNumber = "GRP001", EmployerName = "Acme", BillingDay = 1, GracePeriodDays = 30 },
                        new SponsorDto { GroupNumber = "GRP002", EmployerName = "Beta", BillingDay = 1, GracePeriodDays = 30 },
                    }));
                return req.RequestUri.AbsolutePath.Contains("GRP001", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : Contract(dueDay: 20, graceDays: 60);
            }, answersContractReads: true))
            { BaseAddress = new Uri("http://sponsor-service") });

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        result.Warnings.Should().ContainSingle(w => w.Contains("GRP001"));
        var invoice = _created.Should().ContainSingle().Subject;
        invoice.GroupNumber.Should().Be("GRP002");
        invoice.GracePeriodDays.Should().Be(60);
    }
}
