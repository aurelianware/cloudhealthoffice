using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Security;

/// <summary>
/// Auto-debit end to end through the real premium-billing pipeline, the real
/// <see cref="HttpSponsorBankAccountSource"/> and the shared outbound token
/// handler, against a stand-in sponsor-service that enforces what
/// sponsor-service's <c>GET /api/v1/internal/sponsors/{group}/bank-account</c>
/// enforces: premium-billing-service's service token only (sub and azp), the
/// tenant from the token, the active approved account or 404.
/// A FinanceApprover (payments:approve, not the invoice's maker) releases the
/// debit; the service token only fetches the numbers after that check.
/// </summary>
public class SponsorBankAccountEndToEndTests : IClassFixture<SponsorBankAccountEndToEndTests.Factory>
{
    private const string Tenant = "tenant-1";
    private const string Maker = "finance-user-5";
    private const string Approver = "approver-9";
    private const string Group = "GRP001";
    private const string Routing = "021000021";
    private const string Account = "000123456789";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Mock<IBillingRunRepository> Runs { get; } = new();
        public Mock<IPremiumInvoiceRepository> Invoices { get; } = new();
        public Mock<IEftDraftRepository> Drafts { get; } = new();
        public Mock<IStripeAchService> Stripe { get; } = new();
        public StandInSponsorService SponsorService { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["CosmosDb:Endpoint"] = "",
                ["Nacha:ImmediateDestination"] = "091000019",
                ["Nacha:ImmediateOrigin"] = "1234567890",
                ["Nacha:ImmediateDestinationName"] = "TEST BANK",
                ["Nacha:ImmediateOriginName"] = "CHO PLAN",
                ["Nacha:CompanyName"] = "CHO PLAN",
                ["Nacha:CompanyId"] = "1234567890",
                ["Nacha:OriginatingDfi"] = "09100001",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBillingRunRepository>();
                services.RemoveAll<IPremiumInvoiceRepository>();
                services.RemoveAll<IEftDraftRepository>();
                services.RemoveAll<IStripeAchService>();
                services.AddSingleton(Runs.Object);
                services.AddSingleton(Invoices.Object);
                services.AddSingleton(Drafts.Object);
                services.AddSingleton(Stripe.Object);
                // The real HttpSponsorBankAccountSource stays; only the wire is answered here.
                services.AddHttpClient(HttpSponsorBankAccountSource.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => SponsorService);
            });
        }
    }

    private readonly Factory _factory;
    private readonly List<EftDraft> _drafts = new();

    public SponsorBankAccountEndToEndTests(Factory factory)
    {
        _factory = factory;
        _factory.Runs.Reset();
        _factory.Invoices.Reset();
        _factory.Drafts.Reset();
        _factory.Stripe.Reset();
        _factory.SponsorService.Reset();
        _factory.Logs.Clear();

        _factory.Invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(new PremiumInvoice
        {
            TenantId = Tenant, Id = "inv-1", InvoiceNumber = "INV-GRP001-2026-03", GroupNumber = Group,
            SponsorName = "Acme Co", BillingRunId = "run-1", CreatedBy = Maker,
            Status = InvoiceStatus.Sent, BalanceDue = 1500m, TotalAmount = 1500m
        });
        _factory.Runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(new BillingRun
        {
            TenantId = Tenant, Id = "run-1", CreatedBy = Maker, ExecutedBy = Maker, InvoiceIds = new List<string> { "inv-1" }
        });
        _factory.Runs.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun r) => r);
        _factory.Drafts.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => { d.TenantId = Tenant; lock (_drafts) _drafts.Add(d); return d; });
        _factory.Drafts.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>())).ReturnsAsync((EftDraft d) => d);
    }

    private HttpClient As(string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private void ApprovedAccount(bool eftEnabled = true)
        => _factory.SponsorService.Accounts[Group] = new
        {
            eftEnabled,
            preferredMethod = "Nacha",
            routingNumber = eftEnabled ? Routing : null,
            accountNumber = eftEnabled ? Account : null,
            accountType = "Checking",
            accountHolderName = "Acme Co",
            routingNumberLast4 = "0021",
            accountNumberLast4 = "6789",
        };

    private void ReadOnlyByPremiumBillingServiceToken()
    {
        var calls = _factory.SponsorService.Calls;
        calls.Should().NotBeEmpty();
        calls.Should().OnlyContain(c => c.Subject == "premium-billing-service" && c.IsService && c.Tenant == Tenant
                                        && c.Path == $"/api/v1/internal/sponsors/{Group}/bank-account");
    }

    private void NoNumbersLogged()
    {
        var all = string.Join("\n", _factory.Logs.Messages);
        all.Should().NotContain(Account).And.NotContain(Routing);
    }

    [Fact]
    public async Task ApprovedAccount_ReleasedByAnotherApprover_CreatesTheDraft()
    {
        ApprovedAccount();

        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var draft = _drafts.Should().ContainSingle().Subject;
        draft.GroupNumber.Should().Be(Group);
        draft.Method.Should().Be(EftMethod.Nacha);
        draft.InitiatedBy.Should().Be(Approver);
        draft.AccountNumberLast4.Should().Be("6789");
        draft.RoutingNumberLast4.Should().Be("0021");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(Account).And.NotContain(Routing);
        // The approver's token never reached the full read: premium-billing's own did.
        ReadOnlyByPremiumBillingServiceToken();
        NoNumbersLogged();
    }

    [Fact]
    public async Task ApprovedAccount_Batch_BuildsTheNachaFile_ButNeverReturnsIt()
    {
        ApprovedAccount();

        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts/batch", new { billingRunId = "run-1" });

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("draftsInitiated").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("needsAttention").GetArrayLength().Should().Be(0);
        // The file goes to the bank (here none is configured, so it is held for
        // retrieval); the approver gets the masked summary only.
        var file = json.RootElement.GetProperty("nachaFile");
        file.TryGetProperty("fileContent", out _).Should().BeFalse();
        file.GetProperty("entryCount").GetInt32().Should().Be(1);
        file.GetProperty("entries")[0].GetProperty("accountNumberLast4").GetString().Should().Be("6789");
        body.Should().NotContain(Account).And.NotContain(Routing[..8]);
        ReadOnlyByPremiumBillingServiceToken();
        NoNumbersLogged();
    }

    [Fact]
    public async Task NoApprovedAccount_NeedsAttention_AndNoDraft()
    {
        // The stand-in has no account for the group: 404 NoApprovedAccount.
        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts/batch", new { billingRunId = "run-1" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("draftsInitiated").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("skipped").GetInt32().Should().Be(0);
        var item = json.RootElement.GetProperty("needsAttention").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("reason").GetString().Should().Contain("no approved bank account");
        _drafts.Should().BeEmpty();
        ReadOnlyByPremiumBillingServiceToken();
    }

    [Fact]
    public async Task NoApprovedAccount_SingleDraft_Is400NeedsAttention()
    {
        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no approved bank account").And.Contain("Needs attention");
        _drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task NotEnrolled_IsANormalSkip()
    {
        ApprovedAccount(eftEnabled: false);

        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts/batch", new { billingRunId = "run-1" });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("skipped").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("errors").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("needsAttention").GetArrayLength().Should().Be(0);
        _drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task SponsorServiceRefusal_NeedsAttention()
    {
        ApprovedAccount();
        _factory.SponsorService.Refuse = true;

        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/eft/drafts/batch", new { billingRunId = "run-1" });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = json.RootElement.GetProperty("needsAttention").EnumerateArray().Should().ContainSingle().Subject;
        item.GetProperty("reason").GetString().Should().Contain("refused").And.Contain("403");
        _drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task TheMaker_CannotRelease_AndNoNumbersAreFetched()
    {
        ApprovedAccount();

        // Finance prepared the invoice and holds no payments:approve; a custom
        // role with payments:approve that made the run is refused by maker-checker.
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(Maker, Tenant, new[] { "Custom" },
            new[] { "payments:approve", "billing:read" });
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.SponsorService.Calls.Should().BeEmpty();
        _drafts.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutPaymentsApprove_NoNumbersAreFetched()
    {
        ApprovedAccount();

        var response = await As(Maker, ChoRolePermissions.Finance)
            .PostAsJsonAsync("/api/v1/eft/drafts", new { invoiceId = "inv-1" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.SponsorService.Calls.Should().BeEmpty();
    }

    // ── stand-in ────────────────────────────────────────────────────────

    public sealed record Call(string Path, string? Subject, string? Tenant, bool IsService, HttpStatusCode Status);

    /// <summary>
    /// sponsor-service's service-only full read: premium-billing-service's
    /// service token (cho.service role, sub == azp) or 403; tenant from the token.
    /// </summary>
    public sealed class StandInSponsorService : HttpMessageHandler
    {
        private readonly List<Call> _calls = new();

        public ConcurrentDictionary<string, object> Accounts { get; } = new();

        public bool Refuse { get; set; }

        public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }

        public void Reset()
        {
            lock (_calls) _calls.Clear();
            Accounts.Clear();
            Refuse = false;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? subject = null, tenant = null;
            var isService = false;
            HttpResponseMessage response;
            if (request.Headers.Authorization?.Parameter is not { Length: > 0 } raw)
            {
                response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            else
            {
                var token = new JsonWebToken(raw);
                subject = token.Subject;
                tenant = token.Claims.FirstOrDefault(c => c.Type == ChoClaimTypes.TenantId)?.Value;
                isService = token.Claims.Any(c => c.Type == ChoClaimTypes.Role && c.Value == ChoServiceRole.Name);
                var azp = token.Claims.FirstOrDefault(c => c.Type == "azp")?.Value;
                var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (Refuse || !isService || subject != "premium-billing-service" || azp != subject)
                    response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                else if (segments is not ["api", "v1", "internal", "sponsors", _, "bank-account"])
                    response = new HttpResponseMessage(HttpStatusCode.NotFound);
                else if (Accounts.TryGetValue(segments[4], out var account))
                    response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(account) };
                else
                    response = new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = JsonContent.Create(new { code = "NoApprovedAccount", error = $"Sponsor {segments[4]} has no approved bank account" })
                    };
            }

            lock (_calls) _calls.Add(new Call(request.RequestUri!.AbsolutePath, subject, tenant, isService, response.StatusCode));
            return Task.FromResult(response);
        }
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages.ToArray();

        public void Clear() => _messages.Clear();

        public ILogger CreateLogger(string categoryName) => new Logger(_messages);

        public void Dispose() { }

        private sealed class Logger : ILogger
        {
            private readonly ConcurrentQueue<string> _messages;

            public Logger(ConcurrentQueue<string> messages) => _messages = messages;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => _messages.Enqueue(formatter(state, exception) + (exception == null ? string.Empty : " " + exception));
        }
    }
}
