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
/// The service-only full read capitation-service uses for NACHA credits
/// (<c>GET /api/v1/internal/providers/npi/{npi}/bank-account</c>) through the
/// real pipeline, with the real encryption decorator around the in-memory
/// bank-account store and the Development key ring: capitation-service's
/// service token reads the approved account's full numbers (audited, no
/// numbers logged); every user token, TenantAdmin and PlatformAdmin included,
/// and every other service is refused; a pending-only account is 404.
/// </summary>
public class ProviderBankAccountFullReadPipelineTests : IClassFixture<ProviderBankAccountFullReadPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ProvidersController>
    {
        public Mock<IProviderRepository> Providers { get; } = new();
        public Mock<IProviderVersioningService> Versioning { get; } = new();
        public InMemoryProviderBankAccountRepository BankAccounts { get; set; } = new();
        public ProviderBankAccountDualControlPipelineTests.CapturingLoggerProvider Logs { get; } = new();
        public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "cho-providerbank-keys-" + Guid.NewGuid().ToString("N"));

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
    private const string FullPath = "/api/v1/internal/providers/npi/" + Npi + "/bank-account";
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

    public ProviderBankAccountFullReadPipelineTests(Factory factory)
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

    private HttpClient Capitation => Service("capitation-service");

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
    public async Task CapitationServiceToken_reads_the_approved_accounts_full_numbers_and_the_read_is_audited()
    {
        await ApprovedAccountAsync();

        var response = await Capitation.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("routingNumber").GetString().Should().Be(Routing);
        body.GetProperty("accountNumber").GetString().Should().Be(Account);
        body.GetProperty("accountNumberLast4").GetString().Should().Be("7777");
        body.GetProperty("preferredDisbursementMethod").GetString().Should().Be("NachaCredit");
        body.GetProperty("eftEnabled").GetBoolean().Should().BeTrue();
        body.TryGetProperty("taxId", out _).Should().BeFalse("the bank tax id is never returned");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(TaxId);

        var audit = _factory.Logs.Messages.Where(m => m.Contains("AUDIT provider bank account read for disbursement")).ToList();
        audit.Should().ContainSingle();
        audit[0].Should().Contain("p-1").And.Contain(Npi).And.Contain(Tenant).And.Contain("capitation-service");
    }

    [Fact]
    public async Task The_stored_record_is_encrypted()
    {
        await ApprovedAccountAsync();

        var stored = _factory.BankAccounts.Records.Should().ContainSingle().Subject;
        stored.Active!.AccountNumber.Should().StartWith("enc:v2:").And.NotContain(Account);
        stored.Active.RoutingNumber.Should().StartWith("enc:v2:");
        stored.Active.TaxId.Should().StartWith("enc:v2:");
        stored.Active.AccountNumberLast4.Should().Be("7777");
    }

    [Theory]
    [InlineData("premium-billing-service")]
    [InlineData("payment-service")]
    [InlineData("provider-service")]
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
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.ProviderRelations)]
    public async Task User_tokens_are_forbidden_even_TenantAdmin_and_PlatformAdmin(string role)
    {
        await ApprovedAccountAsync();

        var response = await As("someone", role).GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().NotContain(Account);
    }

    [Fact]
    public async Task A_user_token_named_like_capitation_service_is_forbidden()
    {
        await ApprovedAccountAsync();
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken("capitation-service", Tenant,
            new[] { ChoRolePermissions.TenantAdmin }, new[] { "payments:approve", "providers:read" });
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        (await client.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_pending_only_account_is_404_NoApprovedAccount()
    {
        await ProposeAsync();

        var response = await Capitation.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("NoApprovedAccount").And.NotContain(Account);
    }

    [Fact]
    public async Task A_pending_change_is_never_returned_the_approved_account_is()
    {
        await ApprovedAccountAsync(BankAccount(RowRouting, RowAccount));
        var change = await ProposeAsync();
        change.Status.Should().Be(BankAccountChangeStatus.Pending);

        var body = await Capitation.GetFromJsonAsync<JsonElement>(FullPath);

        // The approved account stays the one payments use until the new change is approved.
        body.GetProperty("accountNumber").GetString().Should().Be(RowAccount);
    }

    [Fact]
    public async Task An_account_on_the_provider_row_from_before_dual_control_is_404_NoApprovedAccount()
    {
        _rowAccount = BankAccount(RowRouting, RowAccount);

        var response = await Capitation.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("NoApprovedAccount").And.NotContain(RowAccount).And.NotContain(RowRouting);
    }

    [Fact]
    public async Task A_record_seeded_from_the_provider_row_is_404_NoApprovedAccount()
    {
        _rowAccount = BankAccount(RowRouting, RowAccount);
        var legacy = new ProviderBankAccountRecord
        {
            TenantId = Tenant, ProviderId = "p-1", ProviderNpi = Npi,
            Active = BankAccount(RowRouting, RowAccount),
            ActiveChangeId = ProviderBankAccountRecord.LegacyChangeId,
        };
        (await _factory.BankAccounts.SaveAsync(legacy, 0)).Should().BeTrue();

        var response = await Capitation.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("NoApprovedAccount").And.NotContain(RowAccount).And.NotContain(RowRouting);
    }

    [Fact]
    public async Task A_row_account_becomes_readable_once_proposed_and_approved_by_another_user()
    {
        _rowAccount = BankAccount(RowRouting, RowAccount);
        var change = await ProposeAsync(BankAccount(RowRouting, RowAccount));
        change.Status.Should().Be(BankAccountChangeStatus.Pending, "sending the row's account is a proposal, not an echo");
        (await Capitation.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var approve = await As("user-approver", ChoRolePermissions.FinanceApprover).PostAsync($"{ChangesPath}/{change.Id}/approve", null);
        approve.StatusCode.Should().Be(HttpStatusCode.OK, await approve.Content.ReadAsStringAsync());

        var body = await Capitation.GetFromJsonAsync<JsonElement>(FullPath);
        body.GetProperty("accountNumber").GetString().Should().Be(RowAccount);
    }

    [Fact]
    public async Task No_account_at_all_is_404_NoApprovedAccount_and_an_unknown_provider_is_404()
    {
        (await Capitation.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await Capitation.GetAsync(FullPath)).Content.ReadAsStringAsync()).Should().Contain("NoApprovedAccount");

        var unknown = await Capitation.GetAsync("/api/v1/internal/providers/npi/9999999999/bank-account");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("ProviderNotFound");
    }

    [Fact]
    public async Task Without_EFT_enabled_no_numbers_are_returned()
    {
        var account = BankAccount();
        account.EftEnabled = false;
        account.PreferredDisbursementMethod = DisbursementMethod.Check;
        await ApprovedAccountAsync(account);

        var body = await Capitation.GetFromJsonAsync<JsonElement>(FullPath);

        body.GetProperty("eftEnabled").GetBoolean().Should().BeFalse();
        body.GetProperty("accountNumber").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("accountNumberLast4").GetString().Should().Be("7777");
    }

    [Fact]
    public async Task Undecryptable_numbers_are_503_never_ciphertext()
    {
        await ApprovedAccountAsync();
        // Ciphertext from a key ring this service does not hold.
        var foreign = new DataProtectionFieldProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), "provider-service");
        var record = _factory.BankAccounts.Records.Single();
        var tampered = await _factory.BankAccounts.GetAsync(Tenant, "p-1");
        tampered!.Active!.AccountNumber = foreign.Protect(Account);
        (await _factory.BankAccounts.SaveAsync(tampered, record.Revision)).Should().BeTrue();

        var response = await Capitation.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("enc:").And.NotContain(Account);
    }

    [Fact]
    public async Task A_request_value_in_the_stored_encrypted_form_is_refused()
    {
        var account = BankAccount();
        account.RoutingNumber = "enc:v1:x";

        var response = await As("user-pr", ChoRolePermissions.ProviderRelations).PutAsJsonAsync(AccountPath, account, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.BankAccounts.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task No_numbers_in_any_log_across_propose_approve_and_full_read()
    {
        await ApprovedAccountAsync();
        await Capitation.GetAsync(FullPath);
        await Service("payment-service").GetAsync(FullPath);
        await As("admin", ChoRolePermissions.TenantAdmin).GetAsync(FullPath);

        var all = string.Join("\n", _factory.Logs.Messages);
        all.Should().NotContain(Account).And.NotContain(Routing).And.NotContain(TaxId);
    }
}
