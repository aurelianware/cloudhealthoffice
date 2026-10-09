using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.ProviderService.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using ProviderService.Controllers;
using ProviderService.Models;
using ProviderService.Repositories;
using ProviderService.Services;

namespace CloudHealthOffice.ProviderService.Tests.Security;

/// <summary>
/// The service-only payee read payment-service uses for FFS NACHA CCD+ credits
/// (<c>GET /api/v1/internal/providers/npi/{npi}/payee-account</c>) through the
/// real pipeline: payment-service's service token reads the approved account's
/// full numbers and payee TIN (audited, nothing logged); capitation-service,
/// other services and every user token are refused; a pending-only account is
/// 404; an account without EFT returns no numbers and no TIN.
/// </summary>
public class ProviderPayeeAccountReadPipelineTests : IClassFixture<ProviderPayeeAccountReadPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ProvidersController>
    {
        public Mock<IProviderRepository> Providers { get; } = new();
        public Mock<IProviderVersioningService> Versioning { get; } = new();
        public InMemoryProviderBankAccountRepository BankAccounts { get; set; } = new();
        public ProviderBankAccountDualControlPipelineTests.CapturingLoggerProvider Logs { get; } = new();
        public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "cho-providerpayee-keys-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("Services:TenantService", "http://tenant-service.cloudhealthoffice/api/v1");
            builder.UseSetting("ProviderVerification:BaseUrl", "http://provider-verification-service");
            builder.UseSetting("FieldProtection:KeyRing:LocalDirectory", KeyDirectory);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                foreach (var hosted in services
                             .Where(d => d.ServiceType == typeof(IHostedService)
                                         && d.ImplementationType?.Namespace?.StartsWith("ProviderService") == true)
                             .ToList())
                {
                    services.Remove(hosted);
                }

                services.RemoveAll<IProviderRepository>();
                services.AddSingleton(_ => Providers.Object);
                services.RemoveAll<IProviderVersioningService>();
                services.AddSingleton(_ => Versioning.Object);
                // The store is in memory; the encryption around it is the real one.
                services.RemoveAll<IProviderBankAccountRepository>();
                services.AddScoped<IProviderBankAccountRepository>(sp => new ProtectedProviderBankAccountRepository(
                    BankAccounts, sp.GetRequiredService<IFieldProtector>(),
                    sp.GetRequiredService<ILogger<ProtectedProviderBankAccountRepository>>()));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(KeyDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string Tenant = "tenant-1";
    private const string Npi = "1234567890";
    private const string FullPath = "/api/v1/internal/providers/npi/" + Npi + "/payee-account";
    private const string AccountPath = "/api/v1/providers/npi/" + Npi + "/bank-account";
    private const string ChangesPath = "/api/v1/providers/npi/" + Npi + "/bank-account-changes";
    private const string Routing = "021000089";
    private const string Account = "999988887777";
    private const string TaxId = "12-3456789";
    private const string RowRouting = "091000019";
    private const string RowAccount = "111122223333";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Factory _factory;
    private ProviderBankAccount? _rowAccount;

    public ProviderPayeeAccountReadPipelineTests(Factory factory)
    {
        _factory = factory;
        _factory.Providers.Reset();
        _factory.Versioning.Reset();
        _factory.BankAccounts = new InMemoryProviderBankAccountRepository();
        _factory.Logs.Clear();
        _rowAccount = null;

        _factory.Providers.Setup(r => r.GetByNPIAsync(Npi)).ReturnsAsync(() => new Provider
        {
            Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = Npi,
            VersionId = "v-1", VersionState = ProviderVersionState.Active,
            BankAccount = _rowAccount,
        });
    }

    private HttpClient As(string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient Service(string clientId, string tenant = Tenant)
    {
        var client = _factory.CreateDefaultClient();
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient Payment => Service("payment-service");

    private static ProviderBankAccount BankAccount(string routing = Routing, string account = Account) => new()
    {
        EftEnabled = true,
        PreferredDisbursementMethod = DisbursementMethod.NachaCredit,
        RoutingNumber = routing,
        AccountNumber = account,
        AccountHolderName = "Sunrise Clinic",
        TaxId = TaxId,
        TaxIdType = TaxIdType.EIN,
    };

    private async Task<BankAccountChangeView> ProposeAsync(ProviderBankAccount? account = null)
    {
        var response = await As("user-pr", ChoRolePermissions.ProviderRelations).PutAsJsonAsync(AccountPath, account ?? BankAccount(), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BankAccountChangeView>(Json))!;
    }

    /// <summary>Dual control: user-pr proposes, a different user with payments:approve approves.</summary>
    private async Task ApprovedAccountAsync(ProviderBankAccount? account = null)
    {
        var change = await ProposeAsync(account);
        var approve = await As("user-approver", ChoRolePermissions.FinanceApprover).PostAsync($"{ChangesPath}/{change.Id}/approve", null);
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PaymentServiceToken_reads_the_approved_accounts_numbers_and_payee_tin_and_the_read_is_audited()
    {
        await ApprovedAccountAsync();

        var response = await Payment.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("routingNumber").GetString().Should().Be(Routing);
        body.GetProperty("accountNumber").GetString().Should().Be(Account);
        body.GetProperty("payeeTaxId").GetString().Should().Be(TaxId);
        body.GetProperty("accountType").GetString().Should().Be("Checking");
        body.GetProperty("eftEnabled").GetBoolean().Should().BeTrue();

        var audit = _factory.Logs.Messages.Where(m => m.Contains("AUDIT provider payee account read for FFS EFT")).ToList();
        audit.Should().ContainSingle();
        audit[0].Should().Contain("p-1").And.Contain(Npi).And.Contain(Tenant).And.Contain("payment-service");
        string.Join("\n", _factory.Logs.Messages).Should().NotContain(Account).And.NotContain(Routing).And.NotContain(TaxId);
    }

    [Theory]
    [InlineData("capitation-service")]
    [InlineData("premium-billing-service")]
    [InlineData("claims-service")]
    public async Task Other_service_tokens_are_forbidden(string clientId)
    {
        await ApprovedAccountAsync();

        var response = await Service(clientId).GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Account);
    }

    [Theory]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    [InlineData(ChoRolePermissions.PlatformAdmin)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    public async Task User_tokens_are_forbidden(string role)
    {
        await ApprovedAccountAsync();

        var response = await As("someone", role).GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_pending_only_account_is_404_NoApprovedAccount()
    {
        await ProposeAsync();

        var response = await Payment.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("NoApprovedAccount").And.NotContain(Account);
    }

    [Fact]
    public async Task Without_EFT_enabled_no_numbers_and_no_tin_are_returned()
    {
        var account = BankAccount();
        account.EftEnabled = false;
        account.PreferredDisbursementMethod = DisbursementMethod.Check;
        await ApprovedAccountAsync(account);

        var body = await Payment.GetFromJsonAsync<JsonElement>(FullPath);

        body.GetProperty("eftEnabled").GetBoolean().Should().BeFalse();
        body.GetProperty("accountNumber").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("payeeTaxId").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
