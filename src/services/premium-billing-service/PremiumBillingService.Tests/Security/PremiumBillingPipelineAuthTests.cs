using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Security;

/// <summary>
/// The real premium-billing-service pipeline (real controllers and services)
/// with the repositories, the sponsor/coverage clients and Stripe replaced.
/// Every caller needs a CHO token; the tenant and the acting user come from it.
/// Reads need billing:read, writes billing:run; ledger changes and delinquency
/// processing need finance:write; releasing debits needs payments:approve from
/// someone who did not prepare the invoice.
/// </summary>
public class PremiumBillingPipelineAuthTests : IClassFixture<PremiumBillingPipelineAuthTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Mock<IBillingRunRepository> Runs { get; } = new();
        public Mock<IPremiumInvoiceRepository> Invoices { get; } = new();
        public Mock<IEftDraftRepository> Drafts { get; } = new();
        public Mock<ISponsorServiceClient> Sponsors { get; } = new();
        public Mock<ICoverageServiceClient> Coverage { get; } = new();
        public Mock<IStripeAchService> Stripe { get; } = new();
        public Mock<ISponsorAccountRepository> Accounts { get; } = new();
        public Mock<IRateTableRepository> RateTables { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["CosmosDb:Endpoint"] = "",
            }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBillingRunRepository>();
                services.RemoveAll<IPremiumInvoiceRepository>();
                services.RemoveAll<IEftDraftRepository>();
                services.RemoveAll<ISponsorServiceClient>();
                services.RemoveAll<ICoverageServiceClient>();
                services.RemoveAll<IStripeAchService>();
                services.RemoveAll<ISponsorAccountRepository>();
                services.RemoveAll<IRateTableRepository>();
                services.AddSingleton(RateTables.Object);
                services.AddSingleton(Accounts.Object);
                services.AddSingleton(Runs.Object);
                services.AddSingleton(Invoices.Object);
                services.AddSingleton(Drafts.Object);
                services.AddSingleton(Sponsors.Object);
                services.AddSingleton(Coverage.Object);
                services.AddSingleton(Stripe.Object);
                // sponsor-service's full bank-account read: no approved account.
                services.AddHttpClient(HttpSponsorBankAccountSource.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new NoApprovedAccountSponsorService());
            });
        }
    }

    /// <summary>sponsor-service answering the full bank-account read with "no approved account".</summary>
    public sealed class NoApprovedAccountSponsorService : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { code = "NoApprovedAccount", error = "Sponsor GRP001 has no approved bank account" })
            });
    }

    private const string Tenant = "tenant-1";
    private const string OtherTenant = "tenant-2";
    private const string User = "finance-user-7";
    private const string Approver = "approver-9";
    private readonly Factory _factory;

    public PremiumBillingPipelineAuthTests(Factory factory)
    {
        _factory = factory;
        _factory.Runs.Reset();
        _factory.Invoices.Reset();
        _factory.Drafts.Reset();
        _factory.Sponsors.Reset();
        _factory.Coverage.Reset();
        _factory.Stripe.Reset();
        _factory.Accounts.Reset();
        _factory.RateTables.Reset();
        _factory.RateTables.Setup(r => r.ListVersionsAsync()).ReturnsAsync(new List<RateTableRecord>());
        _factory.RateTables.Setup(r => r.ListVersionsAsync(It.IsAny<string>())).ReturnsAsync(new List<RateTableRecord>());
        _factory.RateTables.Setup(r => r.CreateVersionAsync(It.IsAny<RateTableRecord>())).ReturnsAsync((RateTableRecord r) => r);
        _factory.Accounts.Setup(a => a.UpdateAsync(It.IsAny<string>(), It.IsAny<Action<SponsorAccount>>()))
            .ReturnsAsync((string group, Action<SponsorAccount> change) =>
            {
                var account = new SponsorAccount { GroupNumber = group };
                change(account);
                return account;
            });

        _factory.Runs.Setup(r => r.SearchAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<BillingRunStatus?>()))
            .ReturnsAsync(new List<BillingRun>());
        _factory.Runs.Setup(r => r.CreateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun r) => r);
        _factory.Runs.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun r) => r);
        _factory.Invoices.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>())).ReturnsAsync((PremiumInvoice i) => i);
        _factory.Invoices.Setup(r => r.ListByMemberAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<PremiumInvoice>());
        _factory.Invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice>());
    }

    private HttpClient Client(string tenant, params string[] roles) => ClientAs(User, tenant, roles);

    private HttpClient ClientAs(string subject, string tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    /// <summary>A user token carrying exactly these permissions (a tenant's custom role).</summary>
    private HttpClient PermissionClient(params string[] permissions)
    {
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(User, Tenant, new[] { "BillingClerk" }, permissions);
        return BearerClient(token);
    }

    private HttpClient BearerClient(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string? TenantInRepository()
        => _factory.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Items["TenantId"] as string;

    private void SetupPreparedInvoice(string preparedBy)
    {
        _factory.Invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(new PremiumInvoice
        {
            TenantId = Tenant,
            Id = "inv-1",
            InvoiceNumber = "INV-GRP001-2026-03",
            GroupNumber = "GRP001",
            BillingRunId = "run-1",
            CreatedBy = preparedBy,
            Status = InvoiceStatus.Sent,
            BalanceDue = 1500m,
            TotalAmount = 1500m
        });
        _factory.Runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(new BillingRun
        {
            TenantId = Tenant,
            Id = "run-1",
            CreatedBy = preparedBy,
            ExecutedBy = preparedBy
        });
    }

    // ── authentication and tenant ─────────────────────────────────────

    [Fact]
    public async Task NoToken_IsRejected()
    {
        var client = _factory.CreateClient();

        (await client.GetAsync("/api/v1/billing-runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/premium-invoices")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync("/api/v1/premium-invoices/process-delinquencies", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        _factory.Runs.VerifyNoOtherCalls();
        _factory.Invoices.Verify(r => r.GetOverdueAsync(), Times.Never);
    }

    [Fact]
    public async Task TenantHeaderWithoutToken_IsRejected()
    {
        // Before: the local TenantMiddleware took the tenant from X-Tenant-ID
        // (or X-Dev-Tenant-ID) with no authentication, and defaulted to
        // "default-tenant" when neither was sent.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);
        client.DefaultRequestHeaders.Add("X-Dev-Tenant-ID", OtherTenant);

        (await client.GetAsync("/api/v1/billing-runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/billing-runs", new { billingPeriod = "2026-03-01" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        _factory.Runs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task HeaderTenantDisagreeingWithToken_IsRejected()
    {
        var client = BearerClient(ChoDevelopmentAuth.UserToken(Tenant, ChoRolePermissions.Finance));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", OtherTenant);

        var response = await client.GetAsync("/api/v1/billing-runs");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Runs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RepositoriesSeeTheTokenTenant_NotTheQuery()
    {
        string? seen = null;
        _factory.Runs.Setup(r => r.SearchAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<BillingRunStatus?>()))
            .Callback(() => seen = TenantInRepository())
            .ReturnsAsync(new List<BillingRun>());

        var response = await Client(Tenant, ChoRolePermissions.Finance)
            .GetAsync($"/api/v1/billing-runs?tenantId={OtherTenant}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seen.Should().Be(Tenant);
    }

    // ── actor from token ──────────────────────────────────────────────

    [Fact]
    public async Task CreateBillingRun_RecordsCreatorFromToken_NotBody()
    {
        BillingRun? created = null;
        _factory.Runs.Setup(r => r.CreateAsync(It.IsAny<BillingRun>()))
            .Callback<BillingRun>(r => created = r)
            .ReturnsAsync((BillingRun r) => r);

        var response = await Client(Tenant, ChoRolePermissions.Finance).PostAsJsonAsync("/api/v1/billing-runs",
            new { billingPeriod = "2026-03-01T00:00:00Z", createdBy = "someone-else" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        created!.CreatedBy.Should().Be(User);
        created.CreatedByIsService.Should().BeFalse();
    }

    [Fact]
    public async Task CreateBillingRun_ByScheduler_RecordsServiceIdentity()
    {
        BillingRun? created = null;
        _factory.Runs.Setup(r => r.CreateAsync(It.IsAny<BillingRun>()))
            .Callback<BillingRun>(r => created = r)
            .ReturnsAsync((BillingRun r) => r);
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("billing-scheduler", Tenant);

        var response = await BearerClient(token).PostAsJsonAsync("/api/v1/billing-runs",
            new { billingPeriod = "2026-03-01T00:00:00Z", createdBy = "a-person" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        created!.CreatedBy.Should().Be("billing-scheduler");
        created.CreatedByIsService.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteBillingRun_RecordsExecutorFromToken_AndCallsOutWithRunTenant()
    {
        var run = new BillingRun { TenantId = Tenant, Id = "run-1", Status = BillingRunStatus.Pending, BillingPeriod = new DateTime(2026, 3, 1) };
        _factory.Runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(run);
        _factory.Sponsors.Setup(s => s.GetActiveSponsorsAsync(Tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SponsorDto>());

        var response = await Client(Tenant, ChoRolePermissions.Finance).PostAsync("/api/v1/billing-runs/run-1/execute", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        run.ExecutedBy.Should().Be(User);
        run.ExecutedByIsService.Should().BeFalse();
        _factory.Sponsors.Verify(s => s.GetActiveSponsorsAsync(Tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecordPayment_RecordsWhoRecordedIt()
    {
        var invoice = new PremiumInvoice { TenantId = Tenant, Id = "inv-1", GroupNumber = "GRP001", Status = InvoiceStatus.Sent };
        invoice.LineItems.Add(new InvoiceLineItem { MemberId = "m1", TotalPremium = 100m });
        _factory.Invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(invoice);
        _factory.Invoices.Setup(r => r.GetByGroupNumberAsync("GRP001")).ReturnsAsync(new[] { invoice });

        var response = await Client(Tenant, ChoRolePermissions.Finance).PostAsJsonAsync(
            "/api/v1/premium-invoices/inv-1/payments", new { amount = 40m, paymentDate = "2026-03-05T00:00:00Z" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        invoice.Payments.Single().RecordedBy.Should().Be(User);
        invoice.LastUpdatedBy.Should().Be(User);
        // The sponsor's account balance is refreshed with the payment.
        _factory.Accounts.Verify(a => a.UpdateAsync("GRP001", It.IsAny<Action<SponsorAccount>>()), Times.Once);
    }

    // ── permissions ───────────────────────────────────────────────────

    [Fact]
    public async Task Finance_ReadsAndRunsBilling()
    {
        var client = Client(Tenant, ChoRolePermissions.Finance);

        (await client.GetAsync("/api/v1/billing-runs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/billing-runs", new { billingPeriod = "2026-03-01T00:00:00Z" })).StatusCode
            .Should().Be(HttpStatusCode.Created);
        (await client.PostAsync("/api/v1/premium-invoices/process-delinquencies", null)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RolesWithoutBilling_CannotReadOrRunBilling()
    {
        // FinanceApprover now reads billing (below); ProviderRelations holds no
        // billing permission at all.
        foreach (var role in new[] { ChoRolePermissions.MemberServices, ChoRolePermissions.ProviderRelations, ChoRolePermissions.ClaimsExaminer })
        {
            var client = Client(Tenant, role);
            (await client.GetAsync("/api/v1/billing-runs")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await client.PostAsJsonAsync("/api/v1/billing-runs", new { billingPeriod = "2026-03-01T00:00:00Z" })).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, role);
        }
        _factory.Runs.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FinanceApprover_ReadsBilling_ButCannotRunItOrChangeTheLedger()
    {
        // The checker reviews the invoices and runs behind the debits it releases.
        var client = ClientAs(Approver, Tenant, ChoRolePermissions.FinanceApprover);

        (await client.GetAsync("/api/v1/billing-runs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/billing-runs", new { billingPeriod = "2026-03-01T00:00:00Z" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/billing-runs/execute", new { billingPeriod = "2026-03-01T00:00:00Z" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/v1/premium-invoices/process-delinquencies", null)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/premium-invoices/inv-1/payments", new { amount = 10m, paymentDate = "2026-03-05" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Runs.Verify(r => r.CreateAsync(It.IsAny<BillingRun>()), Times.Never);
        _factory.Invoices.Verify(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
        _factory.Sponsors.VerifyNoOtherCalls();
    }

    private static object RateTableBody() => new
    {
        table = new
        {
            id = "rt-gold-2026", planId = "PPO-GOLD", effectiveFrom = "2026-01-01", effectiveTo = "2026-12-31", method = "Tier",
            tierRates = new { employeeOnly = 500m, employeeSpouse = 1000m, employeeChildren = 900m, family = 1400m }
        },
        changeReason = "2026 filing"
    };

    [Fact]
    public async Task RateTables_ReadWithBillingRead_ChangeOnlyWithFinanceWrite()
    {
        (await Client(Tenant, ChoRolePermissions.FinanceApprover).GetAsync("/api/v1/rate-tables")).StatusCode.Should().Be(HttpStatusCode.OK);

        // billing:run (run billing, regenerate drafts) is not enough to change what every invoice charges.
        (await PermissionClient("billing:read", "billing:run").PostAsJsonAsync("/api/v1/rate-tables", RateTableBody()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(Tenant, ChoRolePermissions.FinanceApprover).PostAsJsonAsync("/api/v1/rate-tables", RateTableBody()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(Tenant, ChoRolePermissions.MemberServices).GetAsync("/api/v1/rate-tables"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.RateTables.Verify(r => r.CreateVersionAsync(It.IsAny<RateTableRecord>()), Times.Never);

        var created = await Client(Tenant, ChoRolePermissions.Finance).PostAsJsonAsync("/api/v1/rate-tables", RateTableBody());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.RateTables.Verify(r => r.CreateVersionAsync(It.Is<RateTableRecord>(x =>
            x.RateTableId == "rt-gold-2026" && x.Version == 1 && x.CreatedBy == User && x.ContentHash.Length == 64)));
    }

    [Fact]
    public async Task RegeneratingOrIssuingADraft_NeedsBillingRun()
    {
        var approver = ClientAs(Approver, Tenant, ChoRolePermissions.FinanceApprover);

        (await approver.PostAsync("/api/v1/premium-invoices/inv-1/regenerate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await approver.PostAsync("/api/v1/premium-invoices/inv-1/issue", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        _factory.Invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(new PremiumInvoice
        {
            TenantId = Tenant, Id = "inv-1", InvoiceNumber = "INV-GRP001-2026-03", GroupNumber = "GRP001", Status = InvoiceStatus.Sent
        });
        // An issued (unrated) invoice is never regenerated.
        (await Client(Tenant, ChoRolePermissions.Finance).PostAsync("/api/v1/premium-invoices/inv-1/regenerate", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Invoices.Verify(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
    }

    [Fact]
    public async Task MemberPremiumSummary_IsOpenToMemberServices()
    {
        var response = await Client(Tenant, ChoRolePermissions.MemberServices).GetAsync("/api/v1/members/m-1/premium-summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LedgerChangesAndDelinquency_NeedFinanceWrite_NotJustBillingRun()
    {
        // billing:run alone (a custom tenant role) no longer reaches the actions
        // that change balances or suspend sponsors.
        var client = PermissionClient("billing:read", "billing:run");

        (await client.PostAsJsonAsync("/api/v1/premium-invoices/inv-1/payments", new { amount = 10m, paymentDate = "2026-03-05" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/premium-invoices/inv-1/void", new { reason = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/v1/premium-invoices/process-delinquencies", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/v1/eft/drafts/d-1/settle", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/eft/drafts/returns", new { draftId = "d-1", returnCode = "R01" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/v1/eft/drafts/d-1/cancel", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // Still allowed: an ordinary billing write.
        (await client.PostAsJsonAsync("/api/v1/billing-runs", new { billingPeriod = "2026-03-01T00:00:00Z" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        _factory.Invoices.Verify(r => r.UpdateAsync(It.IsAny<PremiumInvoice>()), Times.Never);
        _factory.Drafts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReleasingDebits_NeedsPaymentsApprove_FinanceCannot()
    {
        // Finance prepares; it does not release money (it lacks payments:approve).
        var client = Client(Tenant, ChoRolePermissions.Finance);

        (await client.PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/eft/drafts/batch", new { invoiceIds = new[] { "inv-1" } })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/v1/eft/nacha/generate", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/v1/eft/nacha/held/NACHA-1/retry", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsJsonAsync("/api/v1/eft/nacha/held/NACHA-1/retrieve", new { reason = "x" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        _factory.Invoices.Verify(r => r.GetByIdAsync(It.IsAny<string>()), Times.Never);
        _factory.Drafts.VerifyNoOtherCalls();
        _factory.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReleasingDebit_ByTheUserWhoPreparedTheInvoice_Is403SeparationOfDuties()
    {
        SetupPreparedInvoice(preparedBy: Approver);

        var response = await ClientAs(Approver, Tenant, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Separation of duties");
        _factory.Drafts.Verify(r => r.CreateAsync(It.IsAny<EftDraft>()), Times.Never);
        _factory.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ReleasingDebit_ByServiceToken_Is403()
    {
        SetupPreparedInvoice(preparedBy: User);
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("billing-scheduler", Tenant);

        var response = await BearerClient(token).PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Drafts.Verify(r => r.CreateAsync(It.IsAny<EftDraft>()), Times.Never);
    }

    [Fact]
    public async Task ReleasingDebit_ByAnotherApprover_FailsLoudlyWhenNoAccountIsApproved()
    {
        // Past the maker-checker, the production bank-account source asks
        // sponsor-service; with no approved account the draft is refused as
        // needing attention instead of treated as "not enrolled".
        SetupPreparedInvoice(preparedBy: User);

        var response = await ClientAs(Approver, Tenant, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1", initiatedBy = "someone-else" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no approved bank account").And.Contain("Needs attention");
        _factory.Drafts.Verify(r => r.CreateAsync(It.IsAny<EftDraft>()), Times.Never);
        _factory.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessDelinquencies_WhenSponsorServiceRefusesSuspension_Answers502WithTheFailure()
    {
        var invoice = new PremiumInvoice
        {
            TenantId = Tenant, Id = "inv-9", InvoiceNumber = "INV-9", GroupNumber = "GRP009",
            Status = InvoiceStatus.Sent, BalanceDue = 100m,
            DueDate = DateTime.UtcNow.AddDays(-60), GracePeriodExpires = DateTime.UtcNow.AddDays(-5)
        };
        _factory.Invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });
        _factory.Sponsors.Setup(s => s.SuspendSponsorAsync(Tenant, "GRP009", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SponsorSuspensionOutcome.Failed(403, "sponsor-service answered 403 Forbidden"));

        var response = await Client(Tenant, ChoRolePermissions.Finance).PostAsync("/api/v1/premium-invoices/process-delinquencies", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("delinquentCount").GetInt32().Should().Be(1);
        var failure = body.RootElement.GetProperty("suspensionFailures")[0];
        failure.GetProperty("groupNumber").GetString().Should().Be("GRP009");
        failure.GetProperty("statusCode").GetInt32().Should().Be(403);
        invoice.SponsorSuspension!.State.Should().Be(SponsorSuspensionState.Failed);
        invoice.SponsorSuspension.LastAttemptBy.Should().Be(User);
    }

    [Fact]
    public async Task StripeWebhook_IsAnonymous_ButNeedsAStripeSignature()
    {
        var response = await _factory.CreateClient().PostAsync("/api/v1/eft/webhooks/stripe",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        // Reaches the controller (not 401) and is refused for the missing signature.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Stripe-Signature");
        _factory.Stripe.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NoCorsGrant_ForBrowserOrigins()
    {
        // Called server-to-server only (the portal is Blazor Server).
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/billing-runs");
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await _factory.CreateClient().SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }
}
