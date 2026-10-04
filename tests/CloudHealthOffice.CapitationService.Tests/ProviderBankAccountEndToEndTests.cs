using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CapitationService.Models;
using CapitationService.Services;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.CapitationService.Tests;

/// <summary>
/// The capitation API with the real disbursement service, the real maker-checker
/// rule, the real NACHA credit file builder, the real
/// <see cref="HttpProviderBankAccountSource"/> and the shared outbound token
/// handler, against a stand-in provider-service that enforces what
/// provider-service enforces: the masked read for any CHO token, and the full
/// read (<c>GET /api/v1/internal/providers/npi/{npi}/bank-account</c>) for
/// capitation-service's service token only (sub and azp), tenant from the
/// token, the active approved account or 404 <c>NoApprovedAccount</c>.
/// </summary>
public class ProviderBankAccountEndToEndFactory : CapitationApiFactory
{
    public ITenantPaymentControls PaymentControls { get; } = Substitute.For<ITenantPaymentControls>();
    public StandInProviderService ProviderService { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
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
            var replaced = new[] { typeof(ICapitationDisbursementService), typeof(ITenantPaymentControls), typeof(INachaCreditFileService) };
            foreach (var descriptor in services.Where(d => replaced.Contains(d.ServiceType)).ToList())
                services.Remove(descriptor);

            services.AddSingleton(PaymentControls);
            services.AddSingleton<INachaCreditFileService, NachaCreditFileService>();
            services.AddScoped<ICapitationDisbursementService, CapitationDisbursementService>();
            // The real clients and HttpProviderBankAccountSource stay; only the wire is answered here.
            services.AddHttpClient("ProviderService").ConfigurePrimaryHttpMessageHandler(() => ProviderService);
            services.AddHttpClient(HttpProviderBankAccountSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => ProviderService);
        });
    }
}

public class ProviderBankAccountEndToEndTests : IClassFixture<ProviderBankAccountEndToEndFactory>
{
    private const string Tenant = "tenant-pba";
    private const string Maker = "maker-1";
    private const string Approver = "approver-9";
    private const string Npi = "1234567890";
    private const string Routing = "021000021";
    private const string Account = "000123456789";

    private readonly ProviderBankAccountEndToEndFactory _factory;
    private readonly List<CapitationDisbursement> _created = new();
    private readonly List<CapitationDisbursement> _updated = new();

    public ProviderBankAccountEndToEndTests(ProviderBankAccountEndToEndFactory factory)
    {
        _factory = factory;
        _factory.ProviderService.Reset();
        _factory.Logs.Clear();
        _factory.StatementRepository.ClearReceivedCalls();
        _factory.DisbursementRepository.ClearReceivedCalls();
        _factory.PaymentControls.IsSeparationOfDutiesEnforcedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _factory.StatementRepository.UpdateAsync(Arg.Any<CapitationStatement>()).Returns(ci => ci.Arg<CapitationStatement>());
        _factory.DisbursementRepository.CreateAsync(Arg.Any<CapitationDisbursement>()).Returns(ci =>
        {
            var d = ci.Arg<CapitationDisbursement>();
            d.TenantId = Tenant;
            lock (_created) _created.Add(d);
            return d;
        });
        _factory.DisbursementRepository.UpdateAsync(Arg.Any<CapitationDisbursement>()).Returns(ci =>
        {
            var d = ci.Arg<CapitationDisbursement>();
            lock (_updated) _updated.Add(d);
            return d;
        });
    }

    private void Statement(string id, CapitationStatementStatus status = CapitationStatementStatus.Approved)
        => _factory.StatementRepository.GetByIdAsync(id).Returns(new CapitationStatement
        {
            Id = id, TenantId = Tenant, StatementNumber = $"CAPSTMT-{id}", CapitationRunId = "run-pba",
            ProviderNPI = Npi, ProviderName = "Dr. Smith", Status = status, NetPayable = 1500m,
            CreatedBy = Maker, RunCreatedBy = Maker
        });

    private void PendingNachaDisbursement(string id, string statementId)
    {
        Statement(statementId, CapitationStatementStatus.PaymentInitiated);
        _factory.DisbursementRepository.GetByStatusAsync(DisbursementStatus.Pending).Returns(new List<CapitationDisbursement>
        {
            new() { Id = id, TenantId = Tenant, StatementId = statementId, Method = DisbursementMethod.NachaCredit,
                    Amount = 1500m, ProviderNPI = Npi, ProviderName = "Dr. Smith", Status = DisbursementStatus.Pending }
        });
    }

    private void ApprovedAccount()
        => _factory.ProviderService.Accounts[Npi] = new StandInProviderService.Account(
            EftEnabled: true, PreferredDisbursementMethod: "NachaCredit", RoutingNumber: Routing, AccountNumber: Account);

    private HttpClient As(string subject, params string[] roles) => _factory.CreateTenantClient(Tenant, subject, roles);

    private void FullReadsOnlyByCapitationServiceToken()
    {
        var full = _factory.ProviderService.Calls.Where(c => c.Path.Contains("/internal/")).ToList();
        Assert.NotEmpty(full);
        Assert.All(full, c =>
        {
            Assert.Equal("capitation-service", c.Subject);
            Assert.True(c.IsService);
            Assert.Equal(Tenant, c.Tenant);
            Assert.Equal($"/api/v1/internal/providers/npi/{Npi}/bank-account", c.Path);
        });
    }

    private void NoNumbersLogged()
    {
        var all = string.Join("\n", _factory.Logs.Messages);
        Assert.DoesNotContain(Account, all);
        Assert.DoesNotContain(Routing, all);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task NachaFile_ApprovedAccount_HasTheFullNumbers_FetchedWithCapitationsOwnToken()
    {
        ApprovedAccount();
        PendingNachaDisbursement("d-1", "s-1");

        var response = await As(Approver, ChoRolePermissions.FinanceApprover).PostAsync("/api/v1/capitation/disbursements/nacha-file", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        var file = body.GetProperty("fileContent").GetString()!;
        Assert.Contains(Account, file);
        Assert.Contains(Routing[..8], file);
        Assert.Equal(1, body.GetProperty("entryCount").GetInt32());
        Assert.Equal(0, body.GetProperty("needsAttention").GetArrayLength());
        Assert.Contains(_updated, d => d.Id == "d-1" && d.Status == DisbursementStatus.Submitted);
        // The approver's token never reached the full read: capitation-service's own did.
        FullReadsOnlyByCapitationServiceToken();
        NoNumbersLogged();
    }

    [Fact]
    public async Task Batch_ApprovedAccount_BuildsTheNachaEntryWithTheFullNumbers()
    {
        ApprovedAccount();
        Statement("s-2");

        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/capitation/disbursements/batch", new { statementIds = new[] { "s-2" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(1, body.GetProperty("disbursementsInitiated").GetInt32());
        Assert.Equal(0, body.GetProperty("needsAttention").GetArrayLength());
        var file = body.GetProperty("nachaFile").GetProperty("fileContent").GetString()!;
        Assert.Contains(Account, file);
        var created = Assert.Single(_created);
        Assert.Equal("6789", created.AccountNumberLast4);
        // The masked read carried the user's token; the full read only capitation-service's.
        Assert.Contains(_factory.ProviderService.Calls, c => !c.Path.Contains("/internal/") && c.Subject == Approver);
        FullReadsOnlyByCapitationServiceToken();
        NoNumbersLogged();
    }

    [Fact]
    public async Task NachaFile_NoApprovedAccount_NeedsAttention_NotSkippedSilently()
    {
        // The stand-in has no approved account for the NPI: 404 NoApprovedAccount.
        PendingNachaDisbursement("d-3", "s-3");

        var response = await As(Approver, ChoRolePermissions.FinanceApprover).PostAsync("/api/v1/capitation/disbursements/nacha-file", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await Json(response)).GetProperty("error").GetString()!;
        Assert.Contains("Needs attention", error);
        Assert.Contains("no approved bank account", error);
        var flagged = Assert.Single(_updated);
        Assert.Equal(DisbursementStatus.Pending, flagged.Status);
        Assert.Contains("no approved bank account", flagged.ErrorMessage);
        FullReadsOnlyByCapitationServiceToken();
    }

    [Fact]
    public async Task Batch_NoApprovedAccount_NeedsAttention_AndNoDisbursement()
    {
        Statement("s-4");

        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/capitation/disbursements/batch", new { statementIds = new[] { "s-4" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(0, body.GetProperty("disbursementsInitiated").GetInt32());
        Assert.Equal(0, body.GetProperty("skipped").GetInt32());
        var item = Assert.Single(body.GetProperty("needsAttention").EnumerateArray().ToList());
        Assert.Contains("no approved bank account", item.GetProperty("reason").GetString());
        Assert.Empty(_created);
    }

    [Fact]
    public async Task Batch_MaskedAccountButFullReadRefused_NeedsAttention()
    {
        ApprovedAccount();
        Statement("s-5");
        _factory.ProviderService.RefuseFullRead = true;

        var response = await As(Approver, ChoRolePermissions.FinanceApprover)
            .PostAsJsonAsync("/api/v1/capitation/disbursements/batch", new { statementIds = new[] { "s-5" } });

        var body = await Json(response);
        var item = Assert.Single(body.GetProperty("needsAttention").EnumerateArray().ToList());
        Assert.Contains("refused", item.GetProperty("reason").GetString());
        Assert.Contains("403", item.GetProperty("reason").GetString());
        Assert.Empty(_created);
    }

    [Fact]
    public async Task NachaFile_TheMaker_IsRefused_AndNoNumbersAreFetched()
    {
        ApprovedAccount();
        PendingNachaDisbursement("d-6", "s-6");
        // A custom role with payments:approve that prepared the statement: maker-checker refuses it.
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(Maker, Tenant, new[] { "Custom" },
            new[] { "payments:approve", "payments:read" });
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsync("/api/v1/capitation/disbursements/nacha-file", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.ProviderService.Calls);
    }

    [Fact]
    public async Task NachaFile_WithoutPaymentsApprove_IsRefused_AndNoNumbersAreFetched()
    {
        ApprovedAccount();
        PendingNachaDisbursement("d-7", "s-7");

        var response = await As(Approver, ChoRolePermissions.Finance).PostAsync("/api/v1/capitation/disbursements/nacha-file", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.ProviderService.Calls);
    }

    [Fact]
    public async Task Batch_TheMaker_IsRefused_AndNoNumbersAreFetched()
    {
        ApprovedAccount();
        Statement("s-8");
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(Maker, Tenant, new[] { "Custom" },
            new[] { "payments:approve", "payments:read" });
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/api/v1/capitation/disbursements/batch", new { statementIds = new[] { "s-8" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain(_factory.ProviderService.Calls, c => c.Path.Contains("/internal/"));
        Assert.Empty(_created);
    }
}

// ── stand-in provider-service ──────────────────────────────────────────

public sealed class StandInProviderService : HttpMessageHandler
{
    public sealed record Account(bool EftEnabled, string PreferredDisbursementMethod, string RoutingNumber, string AccountNumber);

    public sealed record Call(string Path, string? Subject, string? Tenant, bool IsService, HttpStatusCode Status);

    private readonly List<Call> _calls = new();

    public ConcurrentDictionary<string, Account> Accounts { get; } = new();

    public bool RefuseFullRead { get; set; }

    public IReadOnlyList<Call> Calls { get { lock (_calls) return _calls.ToList(); } }

    public void Reset()
    {
        lock (_calls) _calls.Clear();
        Accounts.Clear();
        RefuseFullRead = false;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? subject = null, tenant = null;
        var isService = false;
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
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

            if (segments is ["api", "v1", "internal", "providers", "npi", var npi, "bank-account"])
            {
                if (RefuseFullRead || !isService || subject != "capitation-service" || azp != subject)
                    response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                else if (Accounts.TryGetValue(npi, out var account))
                    response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            providerId = "p-" + npi, providerNpi = npi, eftEnabled = account.EftEnabled,
                            preferredDisbursementMethod = account.PreferredDisbursementMethod,
                            routingNumber = account.RoutingNumber, accountNumber = account.AccountNumber,
                            accountType = "Checking", accountHolderName = "Dr. Smith",
                            routingNumberLast4 = account.RoutingNumber[^4..], accountNumberLast4 = account.AccountNumber[^4..],
                        })
                    };
                else
                    response = new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = JsonContent.Create(new { code = "NoApprovedAccount", error = $"Provider {npi} has no approved bank account" })
                    };
            }
            else if (segments is ["api", "providers", "npi", var maskedNpi, "bank-account"])
            {
                response = Accounts.TryGetValue(maskedNpi, out var account)
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            eftEnabled = account.EftEnabled, preferredDisbursementMethod = account.PreferredDisbursementMethod,
                            accountType = "Checking", accountHolderName = "Dr. Smith",
                            routingNumberLast4 = account.RoutingNumber[^4..], accountNumberLast4 = account.AccountNumber[^4..],
                        })
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
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
