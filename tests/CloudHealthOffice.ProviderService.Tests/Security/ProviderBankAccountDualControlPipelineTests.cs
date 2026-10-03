using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Bank-account dual control through the real pipeline (authentication,
/// permissions, controllers): proposing needs providers:write and leaves the
/// active account alone, approving needs payments:approve and a different user,
/// and the masked read capitation-service uses never shows a pending account.
/// The provider and version stores are mocks; the bank-account store is the
/// in-memory fake (no Mongo).
/// </summary>
public class ProviderBankAccountDualControlPipelineTests : IClassFixture<ProviderBankAccountDualControlPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<ProvidersController>
    {
        public Mock<IProviderRepository> Providers { get; } = new();
        public Mock<IProviderVersioningService> Versioning { get; } = new();
        public InMemoryProviderBankAccountRepository BankAccounts { get; set; } = new();
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("MongoDb:ConnectionString", "mongodb://fake-host:27017");
            builder.UseSetting("Services:TenantService", "http://tenant-service.cloudhealthoffice/api/v1");
            builder.UseSetting("ProviderVerification:BaseUrl", "http://provider-verification-service");
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
                services.RemoveAll<IProviderBankAccountRepository>();
                services.AddScoped<IProviderBankAccountRepository>(_ => BankAccounts);
            });
        }
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_messages) return _messages.ToList(); }
        }

        public void Clear()
        {
            lock (_messages) _messages.Clear();
        }

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._messages) owner._messages.Add(formatter(state, exception) + " " + exception);
            }
        }
    }

    private const string Tenant = "tenant-1";
    private const string Npi = "1234567890";
    private const string AccountPath = "/api/v1/providers/npi/" + Npi + "/bank-account";
    // capitation-service reads the legacy prefix.
    private const string CapitationReadPath = "/api/providers/npi/" + Npi + "/bank-account";
    private const string ChangesPath = "/api/v1/providers/npi/" + Npi + "/bank-account-changes";
    private const string OldAccount = "111122223333";
    private const string OldRouting = "091000019";
    private const string NewAccount = "999988887777";
    private const string NewRouting = "021000089";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Factory _factory;

    public ProviderBankAccountDualControlPipelineTests(Factory factory)
    {
        _factory = factory;
        _factory.Providers.Reset();
        _factory.Versioning.Reset();
        _factory.BankAccounts = new InMemoryProviderBankAccountRepository();
        _factory.Logs.Clear();

        _factory.Providers.Setup(r => r.GetByNPIAsync(Npi)).ReturnsAsync(() => new Provider
        {
            Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = Npi,
            VersionId = "v-1", VersionState = ProviderVersionState.Active,
            BankAccount = Account(OldRouting, OldAccount),
        });
    }

    private HttpClient Client(string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient ServiceClient(string clientId)
    {
        var client = _factory.CreateDefaultClient();
        var token = ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, Tenant);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static ProviderBankAccount Account(string routing, string account) => new()
    {
        EftEnabled = true,
        PreferredDisbursementMethod = DisbursementMethod.NachaCredit,
        RoutingNumber = routing,
        AccountNumber = account,
        AccountHolderName = "Sunrise Clinic",
        TaxId = "12-3456789",
        TaxIdType = TaxIdType.EIN,
        W9OnFile = true,
    };

    private HttpClient ProviderRelations(string user = "user-pr") => Client(user, ChoRolePermissions.ProviderRelations);
    private HttpClient FinanceApprover(string user = "user-approver") => Client(user, ChoRolePermissions.FinanceApprover);
    private HttpClient Finance(string user = "user-finance") => Client(user, ChoRolePermissions.Finance);

    private async Task<BankAccountChangeView> ProposeAsync(HttpClient client, string routing = NewRouting, string account = NewAccount)
    {
        var response = await client.PutAsJsonAsync(AccountPath, Account(routing, account), Json);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<BankAccountChangeView>(Json))!;
    }

    private async Task<ProviderBankAccount?> PaymentsReadAsync()
    {
        var response = await Finance().GetAsync(CapitationReadPath);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<ProviderBankAccount>(Json);
    }

    [Fact]
    public async Task Propose_is_pending_and_the_payments_read_still_returns_the_old_account()
    {
        var change = await ProposeAsync(ProviderRelations());

        change.Status.Should().Be(BankAccountChangeStatus.Pending);
        change.RequestedBy.Should().Be("user-pr");
        change.Proposed!.AccountNumber.Should().BeNull("responses are masked");
        change.Proposed.AccountNumberLast4.Should().Be("7777");

        var paid = await PaymentsReadAsync();
        paid!.AccountNumberLast4.Should().Be("3333", "capitation must never see a pending account");
        paid.AccountNumber.Should().BeNull();
    }

    [Fact]
    public async Task Proposing_needs_providers_write()
    {
        var response = await FinanceApprover().PutAsJsonAsync(AccountPath, Account(NewRouting, NewAccount), Json);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.BankAccounts.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task The_payments_read_never_returns_a_pending_first_account()
    {
        _factory.Providers.Setup(r => r.GetByNPIAsync(Npi)).ReturnsAsync(() => new Provider
        {
            Id = "p-1", ProviderId = "p-1", TenantId = Tenant, NPI = Npi,
            VersionId = "v-1", VersionState = ProviderVersionState.Active,
        });
        await ProposeAsync(ProviderRelations());

        (await PaymentsReadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Reviewers_read_the_pending_change_masked()
    {
        await ProposeAsync(ProviderRelations());

        foreach (var client in new[] { FinanceApprover(), ProviderRelations("user-pr-2"), Client("user-examiner", ChoRolePermissions.ClaimsExaminer) })
        {
            var response = await client.GetAsync(ChangesPath + "/pending");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("7777");
            body.Should().NotContain(NewAccount).And.NotContain(NewRouting).And.NotContain("12-3456789");
        }

        var unrelated = await Client("user-ms", ChoRolePermissions.MemberServices).GetAsync(ChangesPath + "/pending");
        unrelated.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_requester_cannot_approve_even_with_payments_approve()
    {
        // TenantAdmin holds both providers:write and payments:approve.
        var admin = Client("user-admin", ChoRolePermissions.TenantAdmin);
        var change = await ProposeAsync(admin);

        var response = await admin.PostAsync($"{ChangesPath}/{change.Id}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Separation of duties");
        (await PaymentsReadAsync())!.AccountNumberLast4.Should().Be("3333");
    }

    [Theory]
    [InlineData(ChoRolePermissions.ProviderRelations)]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.ClaimsSupervisor)]
    public async Task A_user_without_payments_approve_cannot_approve_or_reject(string role)
    {
        var change = await ProposeAsync(ProviderRelations());
        var other = Client("user-other", role);

        (await other.PostAsync($"{ChangesPath}/{change.Id}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await other.PostAsync($"{ChangesPath}/{change.Id}/reject", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PaymentsReadAsync())!.AccountNumberLast4.Should().Be("3333");
    }

    [Fact]
    public async Task A_service_token_cannot_approve()
    {
        var change = await ProposeAsync(ProviderRelations());

        var response = await ServiceClient("capitation-service").PostAsync($"{ChangesPath}/{change.Id}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PaymentsReadAsync())!.AccountNumberLast4.Should().Be("3333");
    }

    [Fact]
    public async Task A_different_finance_approver_approves_and_payments_use_the_new_account()
    {
        var change = await ProposeAsync(ProviderRelations());

        var response = await FinanceApprover().PostAsJsonAsync($"{ChangesPath}/{change.Id}/approve",
            new BankAccountChangeDecisionRequest { Reason = "confirmed with provider" }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var approved = (await response.Content.ReadFromJsonAsync<BankAccountChangeView>(Json))!;
        approved.Status.Should().Be(BankAccountChangeStatus.Approved);
        approved.DecidedBy.Should().Be("user-approver");
        approved.PreviousAccount!.AccountNumberLast4.Should().Be("3333");

        var paid = await PaymentsReadAsync();
        paid!.AccountNumberLast4.Should().Be("7777");
        paid.RoutingNumberLast4.Should().Be("0089");
        _factory.BankAccounts.Records.Single().Active!.AccountNumber
            .Should().Be(NewAccount, "request bodies keep their full numbers; only responses are masked");

        var history = await (await ProviderRelations().GetAsync(ChangesPath)).Content.ReadAsStringAsync();
        history.Should().Contain(change.Id).And.Contain("3333");
        history.Should().NotContain(OldAccount).And.NotContain(NewAccount);
    }

    [Fact]
    public async Task Approving_a_stale_change_is_a_conflict()
    {
        var stale = await ProposeAsync(ProviderRelations());
        await ProposeAsync(ProviderRelations(), account: "555566667777");

        var response = await FinanceApprover().PostAsync($"{ChangesPath}/{stale.Id}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PaymentsReadAsync())!.AccountNumberLast4.Should().Be("3333");
    }

    [Fact]
    public async Task Reject_keeps_the_active_account()
    {
        var change = await ProposeAsync(ProviderRelations());

        var response = await FinanceApprover().PostAsJsonAsync($"{ChangesPath}/{change.Id}/reject",
            new BankAccountChangeDecisionRequest { Reason = "not verified" }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<BankAccountChangeView>(Json))!.Status.Should().Be(BankAccountChangeStatus.Rejected);
        (await PaymentsReadAsync())!.AccountNumberLast4.Should().Be("3333");
        (await FinanceApprover().GetAsync(ChangesPath + "/pending")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_provider_with_an_account_creates_it_without_one_and_the_account_is_pending()
    {
        const string newNpi = "2222222222";
        var created = new Provider
        {
            Id = "p-new", ProviderId = "p-new", TenantId = Tenant, NPI = newNpi, ProviderType = ProviderType.Individual,
            VersionId = "v-1", VersionState = ProviderVersionState.Active,
        };
        _factory.Providers.SetupSequence(r => r.GetByNPIAsync(newNpi))
            .ReturnsAsync((Provider?)null)   // duplicate check on create
            .ReturnsAsync(created)           // payments read
            .ReturnsAsync(created);          // pending read
        Provider? drafted = null;
        _factory.Versioning.Setup(v => v.CreateDraftAsync(It.IsAny<Provider>(), It.IsAny<string>()))
            .ReturnsAsync((Provider p, string _) => { drafted = p; p.ProviderId = "p-new"; p.VersionId = "v-1"; return p; });
        _factory.Versioning.Setup(v => v.ActivateVersionAsync("p-new", "v-1", It.IsAny<string>()))
            .ReturnsAsync(created);

        var response = await ProviderRelations().PostAsJsonAsync("/api/v1/providers", new Provider
        {
            NPI = newNpi, ProviderType = ProviderType.Individual, FirstName = "Ada", LastName = "Lovelace",
            PrimarySpecialty = "Internal Medicine", TaxonomyCode = "207R00000X",
            BankAccount = Account(NewRouting, NewAccount),
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        drafted!.BankAccount.Should().BeNull("the provider row is created without the account");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(NewAccount);

        (await Finance().GetAsync($"/api/providers/npi/{newNpi}/bank-account")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "an unapproved first account is not payable");
        var pending = await FinanceApprover().GetAsync($"/api/v1/providers/npi/{newNpi}/bank-account-changes/pending");
        pending.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = (await pending.Content.ReadFromJsonAsync<BankAccountChangeView>(Json))!;
        view.RequestedBy.Should().Be("user-pr");
        view.Source.Should().Be("POST providers");
    }

    [Fact]
    public async Task No_account_number_reaches_the_logs()
    {
        var change = await ProposeAsync(ProviderRelations());
        await FinanceApprover("user-pr").PostAsync($"{ChangesPath}/{change.Id}/approve", null); // refused: same user
        await FinanceApprover().PostAsync($"{ChangesPath}/{change.Id}/approve", null);
        await PaymentsReadAsync();

        var logs = _factory.Logs.Messages;
        logs.Should().Contain(m => m.Contains("AUDIT bank-account change approved") && m.Contains("user-approver") && m.Contains(Tenant));
        logs.Should().Contain(m => m.Contains("AUDIT bank-account change refused") && m.Contains("user-pr"));
        foreach (var number in new[] { OldAccount, OldRouting, NewAccount, NewRouting, "12-3456789" })
        {
            logs.Should().NotContain(m => m.Contains(number), $"no log entry may carry {number}");
        }
    }
}
