using System.Net;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using static PremiumBillingService.Tests.Services.PremiumBillingServiceTests;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// Defects found when sponsor-service moved to CHO tokens, and the related
/// silent failures in premium billing's outbound calls. Each test fails on the
/// code before the fix.
/// </summary>
public class SponsorCallDefectTests
{
    private const string Tenant = "tenant-1";

    private readonly Mock<IBillingRunRepository> _runs = new();
    private readonly Mock<IPremiumInvoiceRepository> _invoices = new();
    private readonly Mock<IHttpClientFactory> _http = new();
    private readonly Mock<ICurrentActor> _actor = new();
    private readonly ListLogger<PremiumBillingService.Services.PremiumBillingService> _log = new();
    private readonly List<HttpRequestMessage> _sponsorRequests = new();
    private readonly List<HttpRequestMessage> _coverageRequests = new();

    public SponsorCallDefectTests()
    {
        _actor.SetupGet(a => a.UserId).Returns("finance-user");
        _actor.SetupGet(a => a.IsAuthenticated).Returns(true);
        _runs.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun r) => r);
        _invoices.Setup(r => r.CreateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice i) => { i.RecalculateTotals(); return i; });
        _invoices.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>())).ReturnsAsync((PremiumInvoice i) => i);
    }

    private PremiumBillingService.Services.PremiumBillingService Service() => new(
        _runs.Object, _invoices.Object,
        new SponsorServiceClient(_http.Object, NullLogger<SponsorServiceClient>.Instance),
        new CoverageServiceClient(_http.Object, NullLogger<CoverageServiceClient>.Instance),
        _actor.Object, _log);

    private void SponsorService(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _http.Setup(f => f.CreateClient(SponsorServiceClient.HttpClientName)).Returns(() =>
            new HttpClient(new BillingMockHttpMessageHandler(req => { _sponsorRequests.Add(req); return respond(req); }))
            { BaseAddress = new Uri("http://sponsor-service") });

    private void CoverageService(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _http.Setup(f => f.CreateClient(CoverageServiceClient.HttpClientName)).Returns(() =>
            new HttpClient(new BillingMockHttpMessageHandler(req => { _coverageRequests.Add(req); return respond(req); }))
            { BaseAddress = new Uri("http://coverage-service") });

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private BillingRun PendingRun(BillingRunCriteria? criteria = null)
    {
        var run = new BillingRun
        {
            Id = "br-1", TenantId = Tenant, BillingRunNumber = "BR-2026-03-TEST",
            Status = BillingRunStatus.Pending, BillingPeriod = new DateTime(2026, 3, 1),
            Criteria = criteria ?? new BillingRunCriteria()
        };
        _runs.Setup(r => r.GetByIdAsync("br-1")).ReturnsAsync(run);
        return run;
    }

    private static CoverageDto Coverage(string group, string member) => new()
    {
        CoverageId = "cov-" + member, MemberId = member, GroupNumber = group,
        EffectiveDate = new DateTime(2025, 1, 1), MonthlyPremium = 500m, EmployerContribution = 100m
    };

    // ── defect 1: sponsor list shape and paging ───────────────────────

    [Fact]
    public async Task BillingRun_ReadsSponsorServiceListShape_AndFollowsContinuationTokens()
    {
        // Before: the client expected a bare JSON array, so sponsor-service's
        // { sponsors, continuationToken, totalCount } failed every run, and
        // only the first page could ever have been billed.
        PendingRun();
        SponsorService(req => req.RequestUri!.Query.Contains("continuationToken=")
            ? Json(SponsorPage(new[] { new SponsorDto { GroupNumber = "GRP003", EmployerName = "Gamma" } }))
            : Json(SponsorPage(new[]
            {
                new SponsorDto { GroupNumber = "GRP001", EmployerName = "Acme" },
                new SponsorDto { GroupNumber = "GRP002", EmployerName = "Beta" }
            }, continuationToken: "page 2/+")));
        CoverageService(req => Json(CoveragePage(new[] { Coverage("G", "m-" + req.RequestUri!.Query.Length) })));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        result.TotalInvoices.Should().Be(3);
        _sponsorRequests.Should().HaveCount(2);
        _sponsorRequests[0].RequestUri!.Query.Should().Contain("status=Active");
        _sponsorRequests[1].RequestUri!.Query.Should().Contain("continuationToken=" + Uri.EscapeDataString("page 2/+"));
        _sponsorRequests.Should().OnlyContain(r => r.Headers.GetValues("X-Tenant-ID").Single() == Tenant);
    }

    [Fact]
    public async Task BillingRun_ReadsStringEnumsAndBillingInfo_FromSponsorService()
    {
        // sponsor-service writes LineOfBusiness as a string and keeps the
        // billing day and grace period under billingInfo.
        PendingRun(new BillingRunCriteria { LineOfBusiness = LineOfBusiness.Medicare });
        SponsorService(_ => Json(SponsorPage(new[]
        {
            new SponsorDto { GroupNumber = "GRP001", EmployerName = "Acme", LineOfBusiness = LineOfBusiness.Commercial },
            new SponsorDto { GroupNumber = "GRP002", EmployerName = "Beta", LineOfBusiness = LineOfBusiness.Medicare, GracePeriodDays = 45 }
        })));
        CoverageService(_ => Json(CoveragePage(new[] { Coverage("GRP002", "m-1") })));
        PremiumInvoice? created = null;
        _invoices.Setup(r => r.CreateAsync(It.IsAny<PremiumInvoice>()))
            .Callback<PremiumInvoice>(i => created = i)
            .ReturnsAsync((PremiumInvoice i) => { i.RecalculateTotals(); return i; });

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Completed);
        result.TotalInvoices.Should().Be(1);
        created!.GroupNumber.Should().Be("GRP002");
        created.GracePeriodDays.Should().Be(45);
        created.CreatedBy.Should().Be("finance-user");
    }

    [Fact]
    public async Task BillingRun_RepeatedContinuationToken_FailsInsteadOfLooping()
    {
        PendingRun();
        SponsorService(_ => Json(SponsorPage(new[] { new SponsorDto { GroupNumber = "GRP001" } }, continuationToken: "same")));
        CoverageService(_ => Json(CoveragePage(Array.Empty<CoverageDto>())));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Failed);
        result.Errors.Should().ContainSingle(e => e.Contains("continuation token"));
        _invoices.Verify(r => r.CreateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
    }

    [Fact]
    public async Task BillingRun_SponsorServiceRefusal_FailsTheRunWithTheStatus()
    {
        PendingRun();
        SponsorService(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.Status.Should().Be(BillingRunStatus.Failed);
        result.Errors.Should().ContainSingle(e => e.Contains("403"));
    }

    // ── coverage: wrong path and swallowed failures ───────────────────

    [Fact]
    public async Task BillingRun_CoverageFailure_DoesNotProduceAZeroInvoice()
    {
        // Before: GET /api/v1/coverages (coverage-service serves /api/v1/coverage)
        // and any failure was swallowed as "no coverage", so every sponsor got
        // a $0 invoice and the run reported success.
        PendingRun();
        SponsorService(_ => Json(SponsorPage(new[]
        {
            new SponsorDto { GroupNumber = "GRP001", EmployerName = "Acme" },
            new SponsorDto { GroupNumber = "GRP002", EmployerName = "Beta" }
        })));
        CoverageService(req => req.RequestUri!.Query.Contains("GRP001")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json(CoveragePage(new[] { Coverage("GRP002", "m-2") })));

        var result = await Service().ExecuteBillingRunAsync("br-1");

        result.TotalInvoices.Should().Be(1);
        result.Warnings.Should().ContainSingle(w => w.Contains("GRP001") && w.Contains("403"));
        _invoices.Verify(r => r.CreateAsync(It.Is<PremiumInvoice>(i => i.GroupNumber == "GRP001")), Times.Never);
        _coverageRequests.Should().OnlyContain(r => r.RequestUri!.AbsolutePath == "/api/v1/coverage"
                                                    && r.RequestUri.Query.Contains("activeOnly=true")
                                                    && r.Headers.GetValues("X-Tenant-ID").Single() == Tenant);
    }

    // ── defect 3: sponsor suspension failures were swallowed ──────────

    private PremiumInvoice OverdueInvoice(string id = "inv-1", string group = "GRP001") => new()
    {
        TenantId = Tenant, Id = id, InvoiceNumber = "INV-" + id, GroupNumber = group,
        Status = InvoiceStatus.Sent, BalanceDue = 500m,
        DueDate = DateTime.UtcNow.AddDays(-60), GracePeriodExpires = DateTime.UtcNow.AddDays(-10)
    };

    [Fact]
    public async Task Delinquency_SuspensionRefused_IsLoggedRecordedAndReported()
    {
        // Before: TrySuspendSponsorAsync logged a warning and carried on, so a
        // 401/403 from sponsor-service left the sponsor active with nothing
        // recorded and the run reported plain success.
        var invoice = OverdueInvoice();
        _invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });
        SponsorService(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"title\":\"Forbidden\"}") });

        var result = await Service().ProcessDelinquenciesAsync();

        result.DelinquentCount.Should().Be(1);
        result.SponsorsSuspended.Should().Be(0);
        result.SuspensionFailures.Should().ContainSingle(f => f.GroupNumber == "GRP001" && f.StatusCode == 403);
        invoice.Status.Should().Be(InvoiceStatus.Delinquent);
        invoice.SponsorSuspension.Should().NotBeNull();
        invoice.SponsorSuspension!.State.Should().Be(SponsorSuspensionState.Failed);
        invoice.SponsorSuspension.LastStatusCode.Should().Be(403);
        invoice.SponsorSuspension.LastError.Should().Contain("403");
        invoice.SponsorSuspension.LastAttemptBy.Should().Be("finance-user");
        _invoices.Verify(r => r.UpdateAsync(It.Is<PremiumInvoice>(i =>
            i.Id == "inv-1" && i.SponsorSuspension!.State == SponsorSuspensionState.Failed)), Times.Once);
        _log.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("GRP001") && e.Message.Contains("403"));
        _sponsorRequests.Single().Headers.GetValues("X-Tenant-ID").Single().Should().Be(Tenant);
    }

    [Fact]
    public async Task Delinquency_FailedSuspension_IsRetriedOnTheNextRun()
    {
        var invoice = OverdueInvoice();
        invoice.Status = InvoiceStatus.Delinquent;
        invoice.SponsorSuspension = new SponsorSuspensionRecord
        {
            State = SponsorSuspensionState.Failed, Attempts = 1, LastStatusCode = 403, LastError = "403"
        };
        _invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });
        SponsorService(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var result = await Service().ProcessDelinquenciesAsync();

        result.DelinquentCount.Should().Be(0);
        result.SuspensionRetries.Should().Be(1);
        result.SponsorsSuspended.Should().Be(1);
        result.SuspensionFailures.Should().BeEmpty();
        invoice.SponsorSuspension.State.Should().Be(SponsorSuspensionState.Suspended);
        invoice.SponsorSuspension.Attempts.Should().Be(2);
        invoice.SponsorSuspension.LastError.Should().BeNull();
        _sponsorRequests.Single().Method.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task Delinquency_SuspendsEachSponsorOncePerRun()
    {
        var first = OverdueInvoice("inv-1");
        var second = OverdueInvoice("inv-2");
        _invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { first, second });
        SponsorService(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var result = await Service().ProcessDelinquenciesAsync();

        result.DelinquentCount.Should().Be(2);
        _sponsorRequests.Should().ContainSingle();
        first.SponsorSuspension!.State.Should().Be(SponsorSuspensionState.Suspended);
        second.SponsorSuspension!.State.Should().Be(SponsorSuspensionState.Suspended);
    }

    // ── defect 2: sponsor bank account endpoint does not exist ────────

    private readonly Mock<IEftDraftRepository> _drafts = new();
    private readonly Mock<IStripeAchService> _stripe = new();
    private readonly Mock<INachaFileService> _nacha = new();
    private readonly ListLogger<EftDraftService> _eftLog = new();

    private EftDraftService EftService(ISponsorBankAccountSource? source = null, HttpContext? http = null) => new(
        _drafts.Object, _invoices.Object, _runs.Object, _nacha.Object, _stripe.Object,
        source ?? new UnavailableSponsorBankAccountSource(),
        _actor.Object, new HttpContextAccessor { HttpContext = http ?? new DefaultHttpContext() },
        new ConfigurationBuilder().Build(), _eftLog);

    private PremiumInvoice DraftableInvoice(string id = "inv-1") => new()
    {
        TenantId = Tenant, Id = id, InvoiceNumber = "INV-" + id, GroupNumber = "GRP001",
        Status = InvoiceStatus.Sent, BalanceDue = 1500m, TotalAmount = 1500m
    };

    [Fact]
    public async Task BatchDraft_BankDetailsUnavailable_IsAnErrorNeedingAttention_NotASkip()
    {
        // Before: GET sponsors/{group}/bank-account (which does not exist) gave
        // 404, read as "no bank account", and the invoice was counted as a
        // normal skip.
        _invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(DraftableInvoice());
        var run = new BillingRun { Id = "run-1", TenantId = Tenant, InvoiceIds = new List<string> { "inv-1" } };
        _runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);

        var result = await EftService().InitiateBatchDraftAsync(new InitiateBatchEftRequest { BillingRunId = "run-1" });

        result.DraftsInitiated.Should().Be(0);
        result.Skipped.Should().Be(0);
        result.Errors.Should().Be(1);
        result.NeedsAttention.Should().ContainSingle(a => a.InvoiceId == "inv-1" && a.Reason.Contains("unavailable"));
        run.Warnings.Should().ContainSingle(w => w.Contains("inv-1") && w.Contains("needs attention"));
        _runs.Verify(r => r.UpdateAsync(run), Times.Once);
        _eftLog.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("needs attention"));
        _drafts.Verify(r => r.CreateAsync(It.IsAny<EftDraft>()), Times.Never);
    }

    [Fact]
    public async Task SingleDraft_BankDetailsUnavailable_SaysSo_NotEftNotEnabled()
    {
        _invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(DraftableInvoice());

        var act = () => EftService().InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1" });

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("unavailable").And.NotContain("EFT not enabled");
        _eftLog.Entries.Should().Contain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task NachaGeneration_BankDetailsUnavailable_ReportsEveryDraftNeedingAttention()
    {
        _drafts.Setup(r => r.GetByStatusAsync(EftDraftStatus.Pending)).ReturnsAsync(new List<EftDraft>
        {
            new() { Id = "d1", TenantId = Tenant, GroupNumber = "GRP001", Method = EftMethod.Nacha, Amount = 100 }
        });

        var act = () => EftService().GenerateNachaFileForPendingDraftsAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("d1").And.Contain("unavailable");
        _drafts.Verify(r => r.UpdateAsync(It.IsAny<EftDraft>()), Times.Never);
        _eftLog.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("d1"));
    }

    [Fact]
    public async Task Draft_InitiatorComesFromToken_NotTheBody()
    {
        _actor.SetupGet(a => a.UserId).Returns("approver-1");
        _invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(DraftableInvoice());
        _drafts.Setup(r => r.CreateAsync(It.IsAny<EftDraft>())).ReturnsAsync((EftDraft d) => d);
        var source = new Mock<ISponsorBankAccountSource>();
        source.Setup(s => s.GetAsync(Tenant, "GRP001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SponsorBankAccountLookup.Found(new SponsorBankAccount
            {
                EftEnabled = true, RoutingNumber = "091000019", AccountNumber = "123456789"
            }));

        var draft = await EftService(source.Object).InitiateDraftAsync(
            new InitiateEftDraftRequest { InvoiceId = "inv-1", InitiatedBy = "someone-else" });

        draft.InitiatedBy.Should().Be("approver-1");
    }

    [Fact]
    public async Task Draft_ByTheUserWhoExecutedTheBillingRun_IsRefused()
    {
        _actor.SetupGet(a => a.UserId).Returns("finance-user");
        var invoice = DraftableInvoice();
        invoice.BillingRunId = "run-1";
        _invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        _runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(new BillingRun { Id = "run-1", CreatedBy = "other", ExecutedBy = "finance-user" });

        var single = () => EftService().InitiateDraftAsync(new InitiateEftDraftRequest { InvoiceId = "inv-1" });
        var batch = () => EftService().InitiateBatchDraftAsync(new InitiateBatchEftRequest { InvoiceIds = { "inv-1" } });

        await single.Should().ThrowAsync<SeparationOfDutiesException>();
        await batch.Should().ThrowAsync<SeparationOfDutiesException>();
        _drafts.Verify(r => r.CreateAsync(It.IsAny<EftDraft>()), Times.Never);
    }

    // ── Stripe webhook tenant ─────────────────────────────────────────

    [Fact]
    public async Task StripeWebhook_TakesTenantFromSignedEvent_AndIgnoresEventsWithout()
    {
        var http = new DefaultHttpContext();
        var draft = new EftDraft
        {
            Id = "d1", TenantId = Tenant, InvoiceId = "inv-x", Status = EftDraftStatus.Submitted,
            StripePaymentIntentId = "pi_1", Method = EftMethod.StripeAch, Amount = 10
        };
        string? tenantSeen = null;
        _drafts.Setup(r => r.GetByStripePaymentIntentIdAsync("pi_1"))
            .Callback(() => tenantSeen = http.Items["TenantId"] as string)
            .ReturnsAsync(new[] { draft });
        _drafts.Setup(r => r.GetByIdAsync("d1")).ReturnsAsync(draft);
        _drafts.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>())).ReturnsAsync((EftDraft d) => d);
        _stripe.SetupSequence(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new EftWebhookResult { Handled = true, EventType = "payment_succeeded", PaymentIntentId = "pi_1" })
            .ReturnsAsync(new EftWebhookResult { Handled = true, EventType = "payment_succeeded", PaymentIntentId = "pi_1", TenantId = Tenant });

        var service = EftService(http: http);
        await service.ProcessStripeWebhookAsync("{}", "sig");
        _drafts.Verify(r => r.GetByStripePaymentIntentIdAsync(It.IsAny<string>()), Times.Never);

        await service.ProcessStripeWebhookAsync("{}", "sig");
        tenantSeen.Should().Be(Tenant);
        draft.Status.Should().Be(EftDraftStatus.Settled);
        draft.LastUpdatedBy.Should().Be(EftDraftService.StripeWebhookActor);
    }

    // ── no default tenant ─────────────────────────────────────────────

    [Fact]
    public async Task EftDraftRepository_WithoutTenant_Throws_InsteadOfUsingDefault()
    {
        // Before: the EFT draft repositories fell back to tenant "default".
        var database = new MongoClient("mongodb://127.0.0.1:1/?connectTimeoutMS=100&serverSelectionTimeoutMS=100")
            .GetDatabase("pb-no-tenant-test");
        var repository = new EftDraftRepositoryMongo(database, new HttpContextAccessor { HttpContext = new DefaultHttpContext() });

        var act = () => repository.CreateAsync(new EftDraft { Id = "d1" });

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("TenantId");
    }
}

/// <summary>Captures log entries so tests can assert a failure was logged as an error.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
    }
}
