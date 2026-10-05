using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;

namespace PremiumBillingService.Tests.Security;

/// <summary>
/// A Finance user runs billing end to end through the real premium-billing
/// pipeline with the real sponsor and coverage clients and the outbound token
/// handler (the Finance token is forwarded). sponsor-service and
/// coverage-service are stand-ins that enforce the same permissions as their
/// controllers:
/// <list type="bullet">
///   <item>sponsor list: enrollment:read or billing:read;</item>
///   <item>sponsor status (<c>PUT /api/v1/sponsors/{group}/status</c>, body
///   <c>{ status, reason }</c>, Active ↔ Suspended only): finance:write or
///   enrollment:process;</item>
///   <item>full sponsor <c>PUT /api/v1/sponsors/{group}</c>: enrollment:process;</item>
///   <item>coverage search (<c>GET /api/v1/coverage</c>): coverage:read or billing:read.</item>
/// </list>
/// </summary>
public class FinanceBillingEndToEndTests : IClassFixture<FinanceBillingEndToEndTests.Factory>
{
    private const string Tenant = "tenant-1";
    private const string User = "finance-user-5";
    private const string Group = "GRP001";

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Mock<IBillingRunRepository> Runs { get; } = new();
        public Mock<IPremiumInvoiceRepository> Invoices { get; } = new();
        public FakeSponsorService Sponsors { get; } = new();
        public FakeCoverageService Coverage { get; } = new();

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
                services.RemoveAll<IStripeAchService>();
                services.AddSingleton(Runs.Object);
                services.AddSingleton(Invoices.Object);
                services.AddSingleton(Mock.Of<IEftDraftRepository>());
                services.AddSingleton(Mock.Of<IStripeAchService>());
                // The real SponsorServiceClient / CoverageServiceClient stay; only
                // what would go on the wire is answered here.
                services.AddHttpClient(SponsorServiceClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Sponsors);
                services.AddHttpClient(CoverageServiceClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Coverage);
            });
        }
    }

    private readonly Factory _factory;
    private readonly List<PremiumInvoice> _created = new();

    public FinanceBillingEndToEndTests(Factory factory)
    {
        _factory = factory;
        _factory.Runs.Reset();
        _factory.Invoices.Reset();
        _factory.Sponsors.Reset();
        _factory.Coverage.Reset();

        BillingRun? run = null;
        _factory.Runs.Setup(r => r.CreateAsync(It.IsAny<BillingRun>()))
            .ReturnsAsync((BillingRun r) => { r.TenantId = Tenant; run = r; return r; });
        _factory.Runs.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(() => run);
        _factory.Runs.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun r) => r);
        _factory.Invoices.Setup(r => r.CreateAsync(It.IsAny<PremiumInvoice>()))
            .ReturnsAsync((PremiumInvoice i) => { i.TenantId = Tenant; _created.Add(i); return i; });
        _factory.Invoices.Setup(r => r.UpdateAsync(It.IsAny<PremiumInvoice>())).ReturnsAsync((PremiumInvoice i) => i);
        _factory.Invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice>());
    }

    private HttpClient Finance()
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, ChoRolePermissions.Finance));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static PremiumInvoice PastGrace(PremiumInvoice invoice)
    {
        invoice.Status = InvoiceStatus.Sent;
        invoice.DueDate = DateTime.UtcNow.AddDays(-60);
        invoice.GracePeriodExpires = DateTime.UtcNow.AddDays(-5);
        return invoice;
    }

    [Fact]
    public async Task Finance_BillsASponsor_ThenSuspendsItForDelinquency()
    {
        // Bill: sponsors (billing:read) and their coverage (billing:read, new).
        var runResponse = await Finance().PostAsJsonAsync("/api/v1/billing-runs/execute",
            new { billingPeriod = "2026-03-01T00:00:00Z" });

        runResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var run = JsonDocument.Parse(await runResponse.Content.ReadAsStringAsync()))
        {
            run.RootElement.GetProperty("status").ToString().Should().Be(BillingRunStatus.Completed.ToString());
            run.RootElement.GetProperty("totalInvoices").GetInt32().Should().Be(1, string.Join("; ",
                run.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())));
        }
        var invoice = _created.Should().ContainSingle().Subject;
        invoice.GroupNumber.Should().Be(Group);
        invoice.SubtotalPremium.Should().BeGreaterThan(0);
        _factory.Coverage.Calls.Should().ContainSingle(c => c.Subject == User && c.Status == HttpStatusCode.OK);

        // The invoice goes unpaid past its grace period: Finance processes delinquencies.
        _factory.Invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { PastGrace(invoice) });

        var response = await Finance().PostAsync("/api/v1/premium-invoices/process-delinquencies", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("delinquentCount").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("sponsorsSuspended").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("suspensionFailures").GetArrayLength().Should().Be(0);

        invoice.Status.Should().Be(InvoiceStatus.Delinquent);
        invoice.SponsorSuspension!.State.Should().Be(SponsorSuspensionState.Suspended);
        invoice.SponsorSuspension.LastAttemptBy.Should().Be(User);

        var put = _factory.Sponsors.Calls.Where(c => c.Method == HttpMethod.Put).Should().ContainSingle().Subject;
        put.Path.Should().Be($"/api/v1/sponsors/{Group}/status");
        put.Subject.Should().Be(User);
        put.Tenant.Should().Be(Tenant);
        put.Status.Should().Be(HttpStatusCode.OK);
        var sent = JsonNode.Parse(put.Body!)!.AsObject();
        sent.Select(p => p.Key).Should().BeEquivalentTo("status", "reason");
        sent["status"]!.GetValue<string>().Should().Be("Suspended");
        sent["reason"]!.GetValue<string>().Should().Contain(invoice.InvoiceNumber);
        _factory.Sponsors.StatusOf(Group).Should().Be("Suspended");
    }

    [Fact]
    public async Task FailedSuspension_IsRecorded_AndRetriedByFinanceUntilItSucceeds()
    {
        // sponsor-service is down for the first run: recorded, 502.
        var invoice = PastGrace(new PremiumInvoice
        {
            TenantId = Tenant, Id = "inv-1", InvoiceNumber = "INV-GRP001-2026-03", GroupNumber = Group, BalanceDue = 100m
        });
        _factory.Invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });
        _factory.Sponsors.Unavailable = true;

        var first = await Finance().PostAsync("/api/v1/premium-invoices/process-delinquencies", null);

        first.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        invoice.SponsorSuspension!.State.Should().Be(SponsorSuspensionState.Failed);
        invoice.SponsorSuspension.LastStatusCode.Should().Be(503);
        _factory.Sponsors.StatusOf(Group).Should().Be("Active");

        // Next run: retried with the Finance token and accepted.
        _factory.Sponsors.Unavailable = false;

        var second = await Finance().PostAsync("/api/v1/premium-invoices/process-delinquencies", null);

        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        invoice.SponsorSuspension.State.Should().Be(SponsorSuspensionState.Suspended);
        invoice.SponsorSuspension.Attempts.Should().Be(2);
        _factory.Sponsors.StatusOf(Group).Should().Be("Suspended");
    }

    [Fact]
    public async Task RefusedTransition_IsRecordedAsAFailure()
    {
        // A terminated sponsor cannot be suspended; the refusal is not hidden.
        _factory.Sponsors.SetStatus(Group, "Terminated");
        var invoice = PastGrace(new PremiumInvoice
        {
            TenantId = Tenant, Id = "inv-1", InvoiceNumber = "INV-1", GroupNumber = Group, BalanceDue = 100m
        });
        _factory.Invoices.Setup(r => r.GetOverdueAsync()).ReturnsAsync(new List<PremiumInvoice> { invoice });

        var response = await Finance().PostAsync("/api/v1/premium-invoices/process-delinquencies", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        invoice.SponsorSuspension!.State.Should().Be(SponsorSuspensionState.Failed);
        invoice.SponsorSuspension.LastStatusCode.Should().Be(409);
    }

    // ── stand-ins ───────────────────────────────────────────────────────

    public sealed record Call(HttpMethod Method, string Path, string? Body, string? Subject, string? Tenant, HttpStatusCode Status);

    /// <summary>Reads the forwarded CHO token the way the services' policies do.</summary>
    private static (string? Subject, string? Tenant, Func<string, bool> Has) Caller(HttpRequestMessage request)
    {
        var auth = request.Headers.Authorization;
        if (auth?.Parameter is not { Length: > 0 } raw)
            return (null, null, _ => false);
        var token = new JsonWebToken(raw);
        var roles = token.Claims.Where(c => c.Type == ChoClaimTypes.Role).Select(c => c.Value).ToList();
        var permissions = token.Claims.Where(c => c.Type == ChoClaimTypes.Permission).Select(c => c.Value).ToList();
        var isService = roles.Contains(ChoServiceRole.Name);
        return (token.Subject, token.Claims.FirstOrDefault(c => c.Type == ChoClaimTypes.TenantId)?.Value,
            anyOf => isService || anyOf.Split(',').Any(p => ChoRolePermissions.Satisfies(permissions, p)));
    }

    public sealed class FakeSponsorService : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _status = new();
        private readonly List<Call> _calls = new();

        public bool Unavailable { get; set; }

        public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }

        public void Reset()
        {
            lock (_calls) _calls.Clear();
            _status.Clear();
            _status[Group] = "Active";
            Unavailable = false;
        }

        public void SetStatus(string group, string status) => _status[group] = status;

        public string StatusOf(string group) => _status[group];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var (subject, tenant, has) = Caller(request);
            var path = request.RequestUri!.AbsolutePath;
            var response = Answer(request.Method, path, body, subject, has);
            lock (_calls) _calls.Add(new Call(request.Method, path, body, subject, tenant, response.StatusCode));
            return response;
        }

        private HttpResponseMessage Answer(HttpMethod method, string path, string? body, string? subject, Func<string, bool> has)
        {
            if (subject is null) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (Unavailable) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") };

            if (method == HttpMethod.Get && path == "/api/v1/sponsors")
            {
                if (!has("enrollment:read,billing:read")) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                var sponsors = _status.Where(s => s.Value == "Active").Select(s => new
                {
                    groupNumber = s.Key, employerName = "Acme Co", status = s.Value, lineOfBusiness = "Commercial",
                    billingInfo = new { billingDay = 1, gracePeriodDays = 30, paymentMethod = "ACH" }
                });
                return Json(new { sponsors, continuationToken = (string?)null, totalCount = sponsors.Count() });
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // api v1 sponsors {group} [status]
            if (method == HttpMethod.Put && segments.Length == 5 && segments[2] == "sponsors" && segments[4] == "status")
            {
                if (!has("finance:write,enrollment:process")) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                if (!_status.TryGetValue(segments[3], out var current)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var json = body is null ? null : JsonNode.Parse(body) as JsonObject;
                var to = json?["status"]?.GetValue<string>();
                var reason = json?["reason"]?.GetValue<string>();
                if (to is null || string.IsNullOrWhiteSpace(reason)) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                if (to == current) return Json(new { status = to, changed = false });
                if (!((current, to) is ("Active", "Suspended") or ("Suspended", "Active")))
                    return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent($"{current} cannot be set to {to}") };
                _status[segments[3]] = to;
                return Json(new { status = to, changed = true });
            }

            if (method == HttpMethod.Put && segments.Length == 4 && segments[2] == "sponsors")
                return has("enrollment:process")
                    ? Json(new { })
                    : new HttpResponseMessage(HttpStatusCode.Forbidden);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    public sealed class FakeCoverageService : HttpMessageHandler
    {
        private readonly List<Call> _calls = new();

        public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }

        public void Reset() { lock (_calls) _calls.Clear(); }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (subject, tenant, has) = Caller(request);
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response;
            if (subject is null)
                response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            else if (request.Method != HttpMethod.Get || path != "/api/v1/coverage")
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            else if (!has("coverage:read,billing:read"))
                response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            else
                response = Json(new
                {
                    coverage = new[]
                    {
                        new
                        {
                            id = "cov-1", memberId = "M-1", groupNumber = Group, planId = "P-1",
                            effectiveDate = "2025-01-01T00:00:00Z", monthlyPremium = 400m, employerContribution = 100m
                        }
                    },
                    continuationToken = (string?)null,
                    totalCount = 1
                });
            lock (_calls) _calls.Add(new Call(request.Method, path, null, subject, tenant, response.StatusCode));
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
