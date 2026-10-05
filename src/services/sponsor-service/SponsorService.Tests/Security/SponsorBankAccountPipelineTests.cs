using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SponsorService.Models;
using SponsorService.Repositories;
using SponsorService.Tests.Fakes;

namespace SponsorService.Tests.Security;

/// <summary>
/// Sponsor bank accounts through the real sponsor-service pipeline: real
/// controllers, the real SponsorBankAccountService and ProtectedSponsorRepository,
/// the real Data Protection field protector (a temporary local key ring), and
/// in-memory raw storage that keeps exactly what was written.
/// </summary>
public class SponsorBankAccountPipelineTests : IClassFixture<SponsorBankAccountPipelineTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public InMemorySponsorBankAccountRepository Accounts { get; } = new();
        public InMemorySponsorRepository Sponsors { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();
        public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "cho-sponsorbank-keys-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            // Read while Program.cs registers services, so it goes in as a host setting.
            builder.UseSetting("FieldProtection:KeyRing:LocalDirectory", KeyDirectory);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MongoDb:ConnectionString"] = "",
                ["CosmosDb:ConnectionString"] = "",
                ["CosmosDb:Endpoint"] = "",
            }));
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISponsorBankAccountRepository>();
                services.AddSingleton<ISponsorBankAccountRepository>(Accounts);
                // The production decorator over raw in-memory storage.
                services.RemoveAll<ISponsorRepository>();
                services.AddScoped<ISponsorRepository>(sp => new ProtectedSponsorRepository(
                    Sponsors, sp.GetRequiredService<IFieldProtector>(), sp.GetRequiredService<ILogger<ProtectedSponsorRepository>>()));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { Directory.Delete(KeyDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private const string Tenant = "tenant-1";
    private const string Group = "GRP-100";
    private const string SponsorId = "sponsor-id-1";
    private const string Base = $"/api/v1/sponsors/{Group}";
    private const string FullPath = $"/api/v1/internal/sponsors/{Group}/bank-account";
    private const string Routing = "021000021";
    private const string Account = "000123456789";
    private const string Routing2 = "011000015";
    private const string Account2 = "55554444333";
    private const string Proposer = "finance-user-1";
    private const string Approver = "approver-2";

    private readonly Factory _factory;

    public SponsorBankAccountPipelineTests(Factory factory)
    {
        _factory = factory;
        _factory.Accounts.Clear();
        _factory.Sponsors.Clear();
        _factory.Logs.Clear();
        _factory.Sponsors.Put(new Sponsor
        {
            Id = SponsorId, TenantId = Tenant, GroupNumber = Group, EmployerName = "Acme Co", Status = SponsorStatus.Active,
            EffectiveDate = new DateTime(2026, 1, 1)
        });
    }

    // ── clients ─────────────────────────────────────────────────────────

    private HttpClient As(string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private HttpClient WithPermissions(string subject, params string[] permissions)
        => Bearer(ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(subject, Tenant, new[] { "Custom" }, permissions));

    private HttpClient Service(string clientId)
        => Bearer(ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken(clientId, Tenant));

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateDefaultClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient Finance => As(Proposer, ChoRolePermissions.Finance);
    private HttpClient FinanceApprover => As(Approver, ChoRolePermissions.FinanceApprover);
    private HttpClient PremiumBilling => Service("premium-billing-service");

    private static object NachaAccount(string routing = Routing, string account = Account) => new
    {
        eftEnabled = true,
        preferredMethod = "Nacha",
        routingNumber = routing,
        accountNumber = account,
        accountType = "Checking",
        accountHolderName = "Acme Co"
    };

    private async Task<string> ProposeAsync(HttpClient? client = null, object? body = null)
    {
        var response = await (client ?? Finance).PostAsJsonAsync($"{Base}/bank-account-changes", body ?? NachaAccount());
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> ApproveAsync(HttpClient client, string changeId)
        => client.PostAsJsonAsync($"{Base}/bank-account-changes/{changeId}/approve", new { reason = "checked with sponsor" });

    private async Task<string> ApprovedAccountAsync()
    {
        var id = await ProposeAsync();
        (await ApproveAsync(FinanceApprover, id)).StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private static async Task<string> BodyOf(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    private void NoNumbersLogged(params string[] numbers)
    {
        var all = string.Join("\n", _factory.Logs.Messages);
        foreach (var n in numbers.DefaultIfEmpty(Account).Concat(new[] { Account, Routing }))
            all.Should().NotContain(n);
    }

    // ── propose / approve / reject / cancel ─────────────────────────────

    [Fact]
    public async Task FirstAccount_IsPending_UntilApproved()
    {
        var id = await ProposeAsync();

        var view = await Finance.GetFromJsonAsync<JsonElement>($"{Base}/bank-account");
        view.GetProperty("active").ValueKind.Should().Be(JsonValueKind.Null);
        view.GetProperty("pending").GetProperty("id").GetString().Should().Be(id);
        view.GetProperty("pending").GetProperty("status").GetString().Should().Be("Pending");

        // premium billing sees nothing to debit yet
        var full = await PremiumBilling.GetAsync(FullPath);
        full.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await BodyOf(full)).Should().Contain("NoApprovedAccount");

        (await ApproveAsync(FinanceApprover, id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var approved = await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath);
        approved.GetProperty("routingNumber").GetString().Should().Be(Routing);
        approved.GetProperty("accountNumber").GetString().Should().Be(Account);
        approved.GetProperty("eftEnabled").GetBoolean().Should().BeTrue();
        approved.GetProperty("preferredMethod").GetString().Should().Be("Nacha");
        approved.GetProperty("accountNumberLast4").GetString().Should().Be("6789");
    }

    [Fact]
    public async Task Approve_RecordsTheDecision_AndAChangeKeepsTheOldAccountUntilApproved()
    {
        var first = await ApprovedAccountAsync();

        var second = await ProposeAsync(body: NachaAccount(Routing2, Account2));
        // Until approved, premium billing still debits the first account.
        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account);

        var response = await ApproveAsync(FinanceApprover, second);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var change = await response.Content.ReadFromJsonAsync<JsonElement>();
        change.GetProperty("status").GetString().Should().Be("Approved");
        change.GetProperty("decidedBy").GetString().Should().Be(Approver);
        change.GetProperty("requestedBy").GetString().Should().Be(Proposer);
        change.GetProperty("previousAccount").GetProperty("accountNumberLast4").GetString().Should().Be("6789");
        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account2);

        var history = await FinanceApprover.GetFromJsonAsync<JsonElement>($"{Base}/bank-account-changes");
        history.EnumerateArray().Select(c => c.GetProperty("id").GetString()).Should().Equal(second, first);
        NoNumbersLogged(Account2, Routing2);
    }

    [Fact]
    public async Task Reject_LeavesTheActiveAccount()
    {
        await ApprovedAccountAsync();
        var id = await ProposeAsync(body: NachaAccount(Routing2, Account2));

        var response = await FinanceApprover.PostAsJsonAsync($"{Base}/bank-account-changes/{id}/reject", new { reason = "not verified" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Rejected");
        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account);
        (await ApproveAsync(FinanceApprover, id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Cancel_WithdrawsThePendingChange()
    {
        var id = await ProposeAsync();

        var response = await Finance.PostAsJsonAsync($"{Base}/bank-account-changes/{id}/cancel", new { reason = "typo" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString().Should().Be("Cancelled");
        (await FinanceApprover.GetAsync($"{Base}/bank-account-changes/pending")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ApproveAsync(FinanceApprover, id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await PremiumBilling.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ANewProposal_SupersedesThePendingOne()
    {
        var first = await ProposeAsync();
        var second = await ProposeAsync(body: NachaAccount(Routing2, Account2));

        (await ApproveAsync(FinanceApprover, first)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApproveAsync(FinanceApprover, second)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]              // billing:run
    [InlineData(ChoRolePermissions.EnrollmentSpecialist)] // enrollment:process
    public async Task Propose_NeedsBillingRunOrEnrollmentProcess(string role)
    {
        (await As("someone", role).PostAsJsonAsync($"{Base}/bank-account-changes", NachaAccount()))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Theory]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]
    [InlineData(ChoRolePermissions.ProviderRelations)]
    public async Task Propose_IsForbiddenToOtherRoles(string role)
    {
        (await As("someone", role).PostAsJsonAsync($"{Base}/bank-account-changes", NachaAccount()))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Accounts.RawJson(Tenant, Group).Should().BeNull();
    }

    [Fact]
    public async Task Approve_NeedsPaymentsApprove()
    {
        var id = await ProposeAsync();

        (await ApproveAsync(As("other-finance", ChoRolePermissions.Finance), id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ApproveAsync(As("enroller", ChoRolePermissions.EnrollmentSpecialist), id)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PremiumBilling.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("tenant-admin")]
    public async Task TheProposer_CannotApprove(string who)
    {
        var client = who == "custom"
            ? WithPermissions("maker-checker-1", "billing:run", "payments:approve", "billing:read")
            : As("admin-1", ChoRolePermissions.TenantAdmin);
        var id = await ProposeAsync(client);

        var response = await ApproveAsync(client, id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await BodyOf(response)).Should().Contain("Separation of duties");
        (await PremiumBilling.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Logs.Messages.Should().Contain(m => m.Contains("AUDIT sponsor bank-account change refused") && m.Contains("separation of duties"));
    }

    [Theory]
    [InlineData("premium-billing-service")]
    [InlineData("enrollment-import-service")]
    public async Task AServiceToken_CannotApproveOrReject(string clientId)
    {
        var id = await ProposeAsync();

        var approve = await ApproveAsync(Service(clientId), id);
        var reject = await Service(clientId).PostAsJsonAsync($"{Base}/bank-account-changes/{id}/reject", new { });

        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await BodyOf(approve)).Should().Contain("Separation of duties");
        reject.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PremiumBilling.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StaleApproval_WhenTheActiveAccountChanged_Is409()
    {
        var id = await ProposeAsync();
        // Another writer switched the active account after the proposal.
        var raw = _factory.Accounts.Raw(Tenant, Group)!;
        raw.ActiveChangeId = "some-other-change";
        _factory.Accounts.Put(raw);

        var response = await ApproveAsync(FinanceApprover, id);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await BodyOf(response)).Should().Contain("has changed since");
    }

    [Fact]
    public async Task ConcurrentWrite_DuringApproval_Is409()
    {
        var id = await ProposeAsync();
        var raced = false;
        _factory.Accounts.BeforeSave = () =>
        {
            if (raced) return;
            raced = true;
            var raw = _factory.Accounts.Raw(Tenant, Group)!;
            raw.Revision++;
            _factory.Accounts.Put(raw);
        };

        var response = await ApproveAsync(FinanceApprover, id);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Accounts.Raw(Tenant, Group)!.Active.Should().BeNull();
    }

    [Fact]
    public async Task AlreadyApproved_Is409()
    {
        var id = await ApprovedAccountAsync();

        (await ApproveAsync(As("approver-3", ChoRolePermissions.FinanceApprover), id)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("""{"eftEnabled":true,"preferredMethod":"Nacha","routingNumber":"123456789","accountNumber":"12345678"}""")] // bad check digit
    [InlineData("""{"eftEnabled":true,"preferredMethod":"Nacha","routingNumber":"021000021"}""")]                         // one number only
    [InlineData("""{"eftEnabled":true,"preferredMethod":"Nacha"}""")]                                                   // no account to keep
    [InlineData("""{"eftEnabled":true,"preferredMethod":"StripeAch"}""")]                                               // no Stripe ids
    [InlineData("""{"eftEnabled":true,"routingNumber":"021000021","accountNumber":"12ab"}""")]                           // not digits
    public async Task InvalidProposal_Is400(string json)
    {
        var response = await Finance.PostAsync($"{Base}/bank-account-changes",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.Accounts.RawJson(Tenant, Group).Should().BeNull();
    }

    [Fact]
    public async Task EnrollmentOnlyChange_KeepsTheApprovedNumbers()
    {
        await ApprovedAccountAsync();

        var id = await ProposeAsync(body: new { eftEnabled = false, preferredMethod = "Nacha", accountHolderName = "Acme Co" });
        (await ApproveAsync(FinanceApprover, id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var record = await _factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<SponsorService.Services.ISponsorBankAccountService>()
            .GetAsync(new Sponsor { Id = SponsorId, TenantId = Tenant, GroupNumber = Group });
        record!.Active!.EftEnabled.Should().BeFalse();
        record.Active.AccountNumber.Should().Be(Account);
        // Not enrolled: the full read returns no numbers.
        var full = await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath);
        full.GetProperty("eftEnabled").GetBoolean().Should().BeFalse();
        full.TryGetProperty("accountNumber", out var n).Should().BeTrue();
        n.ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ── reads ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ChoRolePermissions.Finance)]              // billing:read
    [InlineData(ChoRolePermissions.FinanceApprover)]      // payments:read, payments:approve
    [InlineData(ChoRolePermissions.EnrollmentSpecialist)] // enrollment:read
    [InlineData(ChoRolePermissions.TenantAdmin)]
    [InlineData(ChoRolePermissions.ComplianceOfficer)]    // *:read
    public async Task Reads_AreMasked(string role)
    {
        await ApprovedAccountAsync();
        await ProposeAsync(body: NachaAccount(Routing2, Account2));
        var client = As("reader", role);

        foreach (var path in new[] { $"{Base}/bank-account", $"{Base}/bank-account-changes", $"{Base}/bank-account-changes/pending" })
        {
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            var body = await BodyOf(response);
            body.Should().NotContain(Account).And.NotContain(Routing).And.NotContain(Account2).And.NotContain(Routing2);
            body.Should().NotContain("enc:");
        }

        var view = await client.GetFromJsonAsync<JsonElement>($"{Base}/bank-account");
        view.GetProperty("active").GetProperty("accountNumberLast4").GetString().Should().Be("6789");
        view.GetProperty("active").GetProperty("routingNumberLast4").GetString().Should().Be("0021");
        view.GetProperty("pending").GetProperty("proposed").GetProperty("accountNumberLast4").GetString().Should().Be("4333");
    }

    [Fact]
    public async Task ProposeAndDecisionResponses_AreMasked()
    {
        var propose = await Finance.PostAsJsonAsync($"{Base}/bank-account-changes", NachaAccount());
        var body = await BodyOf(propose);
        body.Should().NotContain(Account).And.NotContain(Routing);
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;

        (await BodyOf(await ApproveAsync(FinanceApprover, id))).Should().NotContain(Account).And.NotContain(Routing);
    }

    [Theory]
    [InlineData(ChoRolePermissions.MemberServices)]
    [InlineData(ChoRolePermissions.ProviderRelations)]
    [InlineData(ChoRolePermissions.UMCoordinator)]
    public async Task Reads_NeedABillingPaymentsOrEnrollmentPermission(string role)
    {
        await ApprovedAccountAsync();

        (await As("reader", role).GetAsync($"{Base}/bank-account")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await As("reader", role).GetAsync($"{Base}/bank-account-changes")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── the full-number read ────────────────────────────────────────────

    [Fact]
    public async Task FullRead_PremiumBillingServiceToken_GetsTheNumbers_AndIsAudited()
    {
        await ApprovedAccountAsync();

        var response = await PremiumBilling.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await BodyOf(response);
        body.Should().Contain(Account).And.Contain(Routing);
        _factory.Logs.Messages.Should().Contain(m => m.Contains("AUDIT sponsor bank account read for debit") && m.Contains("premium-billing-service"));
        NoNumbersLogged();
    }

    [Theory]
    [InlineData("capitation-service")]
    [InlineData("enrollment-import-service")]
    [InlineData("sponsor-service")]
    public async Task FullRead_OtherServiceTokens_AreForbidden(string clientId)
    {
        await ApprovedAccountAsync();

        var response = await Service(clientId).GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await BodyOf(response)).Should().NotContain(Account);
    }

    [Theory]
    [InlineData(ChoRolePermissions.TenantAdmin)]
    [InlineData(ChoRolePermissions.PlatformAdmin)]
    [InlineData(ChoRolePermissions.Finance)]
    [InlineData(ChoRolePermissions.FinanceApprover)]
    [InlineData(ChoRolePermissions.EnrollmentSpecialist)]
    public async Task FullRead_UserTokens_AreForbidden_EvenTenantAdmin(string role)
    {
        await ApprovedAccountAsync();

        var response = await As("someone", role).GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await BodyOf(response)).Should().NotContain(Account);
    }

    [Fact]
    public async Task FullRead_AUserTokenNamedLikePremiumBilling_IsForbidden()
    {
        await ApprovedAccountAsync();
        var token = ChoDevelopmentAuth.UserTokenIssuer().IssueUserToken(
            "premium-billing-service", Tenant, new[] { ChoRolePermissions.TenantAdmin });

        (await Bearer(token).GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task FullRead_IsScopedToTheTokensTenant()
    {
        await ApprovedAccountAsync();
        var otherTenant = Bearer(ChoDevelopmentAuth.ServiceTokenIssuer().IssueServiceToken("premium-billing-service", "tenant-2"));

        (await otherTenant.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FullRead_AccountOfAnEarlierSponsorDocument_IsNotUsed()
    {
        await ApprovedAccountAsync();
        // The sponsor was deleted and re-created under the same group number.
        await _factory.Sponsors.DeleteAsync(Tenant, SponsorId);
        _factory.Sponsors.Put(new Sponsor { Id = "sponsor-id-2", TenantId = Tenant, GroupNumber = Group, EmployerName = "New Co" });

        (await PremiumBilling.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // The new sponsor cannot inherit the old numbers, only propose its own.
        (await Finance.PostAsJsonAsync($"{Base}/bank-account-changes", new { eftEnabled = true, preferredMethod = "Nacha" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var id = await ProposeAsync(body: NachaAccount(Routing2, Account2));
        (await ApproveAsync(FinanceApprover, id)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account2);
    }

    // ── encryption at rest ──────────────────────────────────────────────

    [Fact]
    public async Task Numbers_AreEncryptedInTheStoredDocument()
    {
        var id = await ProposeAsync();

        var pendingRaw = _factory.Accounts.RawJson(Tenant, Group)!;
        pendingRaw.Should().NotContain(Account).And.NotContain(Routing);
        var pending = _factory.Accounts.Raw(Tenant, Group)!.GetPending()!.Proposed!;
        pending.AccountNumber.Should().StartWith("enc:v2:").And.NotBe(Account);
        pending.RoutingNumber.Should().StartWith("enc:v2:").And.NotBe(Routing);

        (await ApproveAsync(FinanceApprover, id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = _factory.Accounts.RawJson(Tenant, Group)!;
        raw.Should().NotContain(Account).And.NotContain(Routing);
        var active = _factory.Accounts.Raw(Tenant, Group)!.Active!;
        active.AccountNumber.Should().StartWith("enc:v2:");
        active.RoutingNumber.Should().StartWith("enc:v2:");
        // Decided changes keep no numbers at all, encrypted or not.
        _factory.Accounts.Raw(Tenant, Group)!.Changes.Single().Proposed!.AccountNumber.Should().BeNull();
    }

    [Fact]
    public async Task LegacyPlaintextBankAccount_IsRead_AndReEncryptedOnTheNextWrite()
    {
        _factory.Accounts.Put(new SponsorBankAccountRecord
        {
            TenantId = Tenant, GroupNumber = Group, SponsorId = SponsorId, Revision = 1,
            ActiveChangeId = "legacy", ActiveApprovedBy = "migration",
            Active = new SponsorBankAccountDetails
            {
                EftEnabled = true, PreferredMethod = SponsorDebitMethod.Nacha, RoutingNumber = Routing, AccountNumber = Account,
                RoutingNumberLast4 = "0021", AccountNumberLast4 = "6789"
            }
        });

        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account);

        await ProposeAsync(body: new { eftEnabled = true, preferredMethod = "Nacha", accountHolderName = "Acme" });

        var raw = _factory.Accounts.RawJson(Tenant, Group)!;
        raw.Should().NotContain(Account).And.NotContain(Routing);
        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account);
    }

    [Fact]
    public async Task TamperedCiphertext_IsNotReturned()
    {
        await ApprovedAccountAsync();
        var raw = _factory.Accounts.Raw(Tenant, Group)!;
        raw.Active!.AccountNumber = raw.Active.AccountNumber![..^4] + "AAAA";
        _factory.Accounts.Put(raw);

        var response = await PremiumBilling.GetAsync(FullPath);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ── binding: a ciphertext only decrypts on its own record and field ──

    private const string OtherGroup = "GRP-101";

    private void PutOtherSponsor() => _factory.Sponsors.Put(new Sponsor
    {
        Id = "sponsor-id-2", TenantId = Tenant, GroupNumber = OtherGroup, EmployerName = "Other Co", Status = SponsorStatus.Active,
        EffectiveDate = new DateTime(2026, 1, 1)
    });

    [Fact]
    public async Task CiphertextCopiedToAnotherSponsorsRecord_DoesNotDecrypt()
    {
        await ApprovedAccountAsync();
        PutOtherSponsor();
        var source = _factory.Accounts.Raw(Tenant, Group)!;
        _factory.Accounts.Put(new SponsorBankAccountRecord
        {
            TenantId = Tenant, GroupNumber = OtherGroup, SponsorId = "sponsor-id-2", Revision = 1,
            ActiveChangeId = "copied", ActiveApprovedBy = "attacker", Active = source.Active,
        });

        var response = await PremiumBilling.GetAsync($"/api/v1/internal/sponsors/{OtherGroup}/bank-account");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await BodyOf(response)).Should().NotContain(Account);
        // The original record still reads.
        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account);
    }

    [Fact]
    public async Task CiphertextMovedToTheOtherField_DoesNotDecrypt()
    {
        await ApprovedAccountAsync();
        var raw = _factory.Accounts.Raw(Tenant, Group)!;
        (raw.Active!.RoutingNumber, raw.Active.AccountNumber) = (raw.Active.AccountNumber, raw.Active.RoutingNumber);
        _factory.Accounts.Put(raw);

        (await PremiumBilling.GetAsync(FullPath)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task UnboundV1Values_StillRead_AndAreBoundOnTheNextWrite()
    {
        // As written before binding existed: the same key ring and purpose, no record context.
        var protector = _factory.Services.GetRequiredService<IFieldProtector>();
        _factory.Accounts.Put(new SponsorBankAccountRecord
        {
            TenantId = Tenant, GroupNumber = Group, SponsorId = SponsorId, Revision = 1,
            ActiveChangeId = "v1", ActiveApprovedBy = "earlier-build",
            Active = new SponsorBankAccountDetails
            {
                EftEnabled = true, PreferredMethod = SponsorDebitMethod.Nacha,
                RoutingNumber = protector.Protect(Routing), AccountNumber = protector.Protect(Account),
                RoutingNumberLast4 = "0021", AccountNumberLast4 = "6789"
            }
        });
        _factory.Accounts.Raw(Tenant, Group)!.Active!.AccountNumber.Should().StartWith("enc:v1:");

        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account);

        await ProposeAsync(body: new { eftEnabled = true, preferredMethod = "Nacha", accountHolderName = "Acme" });
        _factory.Accounts.Raw(Tenant, Group)!.Active!.AccountNumber.Should().StartWith("enc:v2:");
        (await PremiumBilling.GetFromJsonAsync<JsonElement>(FullPath)).GetProperty("accountNumber").GetString().Should().Be(Account);
    }

    [Fact]
    public async Task BillingAccountNumberCopiedToAnotherSponsor_DoesNotDecrypt()
    {
        var create = await As("enroller", ChoRolePermissions.EnrollmentSpecialist).PostAsJsonAsync("/api/v1/sponsors", new
        {
            groupNumber = "GRP-400", employerName = "Delta Co", effectiveDate = "2026-01-01T00:00:00Z",
            billingInfo = new { premiumAmount = 100m, billingDay = 1, billingAccountNumber = "4444333322221111", paymentMethod = "ACH" }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await BodyOf(create));
        var ciphertext = _factory.Sponsors.RawByGroup(Tenant, "GRP-400")!.BillingInfo!.BillingAccountNumber;
        _factory.Sponsors.Put(new Sponsor
        {
            Id = "sponsor-id-5", TenantId = Tenant, GroupNumber = "GRP-500", EmployerName = "Epsilon Co",
            BillingInfo = new BillingInfo { PremiumAmount = 10m, BillingAccountNumber = ciphertext }
        });

        var act = () => ScopedSponsorsAsync(r => r.GetByGroupNumberAsync(Tenant, "GRP-500"));

        await act.Should().ThrowAsync<FieldProtectionException>();
        (await ScopedSponsorsAsync(r => r.GetByGroupNumberAsync(Tenant, "GRP-400")))!.BillingInfo!.BillingAccountNumber
            .Should().Be("4444333322221111");
    }

    [Fact]
    public async Task RejectPlaintext_RefusesALegacyPlaintextBillingAccountNumber()
    {
        _factory.Sponsors.Put(new Sponsor
        {
            Id = "legacy-9", TenantId = Tenant, GroupNumber = "GRP-900", EmployerName = "Legacy Co",
            BillingInfo = new BillingInfo { PremiumAmount = 50m, BillingAccountNumber = "987654321012" }
        });
        var keys = _factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var rejecting = new ProtectedSponsorRepository(_factory.Sponsors,
            new DataProtectionFieldProtector(keys, "sponsor-service", rejectPlaintext: true),
            _factory.Services.GetRequiredService<ILogger<ProtectedSponsorRepository>>());

        var act = () => rejecting.GetByGroupNumberAsync(Tenant, "GRP-900");

        (await act.Should().ThrowAsync<FieldProtectionException>()).Which.Message.Should().Contain("RejectPlaintext").And.NotContain("987654321012");
    }

    // ── the sponsor's BillingInfo account number ────────────────────────

    [Fact]
    public async Task BillingAccountNumber_IsEncryptedAtRest_AndMaskedInEveryResponse()
    {
        const string billingAccount = "BA-998877665544";
        var client = As("enroller", ChoRolePermissions.EnrollmentSpecialist);

        var create = await client.PostAsJsonAsync("/api/v1/sponsors", new
        {
            groupNumber = "GRP-200", employerName = "Beta Co", effectiveDate = "2026-01-01T00:00:00Z",
            billingInfo = new { premiumAmount = 100m, billingDay = 1, billingAccountNumber = billingAccount, paymentMethod = "ACH" }
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await BodyOf(create));
        (await BodyOf(create)).Should().NotContain(billingAccount).And.Contain("5544");

        var stored = _factory.Sponsors.RawByGroup(Tenant, "GRP-200")!;
        stored.BillingInfo!.BillingAccountNumber.Should().StartWith("enc:v2:").And.NotBe(billingAccount);

        foreach (var path in new[] { "/api/v1/sponsors/GRP-200", "/api/v1/sponsors" })
        {
            var body = await BodyOf(await As("finance", ChoRolePermissions.Finance).GetAsync(path));
            body.Should().NotContain(billingAccount).And.NotContain("enc:").And.Contain("\"billingAccountNumberLast4\":\"5544\"");
        }

        // A client that PUTs back what it read (no number) keeps the stored one.
        var update = await client.PutAsJsonAsync("/api/v1/sponsors/GRP-200", new
        {
            contactName = "Pat", billingInfo = new { premiumAmount = 120m, billingDay = 1, paymentMethod = "ACH" }
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyOf(update)).Should().NotContain(billingAccount).And.Contain("5544");
        var reread = await ScopedSponsorsAsync(r => r.GetByGroupNumberAsync(Tenant, "GRP-200"));
        reread!.BillingInfo!.BillingAccountNumber.Should().Be(billingAccount);
        reread.BillingInfo.PremiumAmount.Should().Be(120m);
        NoNumbersLogged(billingAccount);
    }

    [Fact]
    public async Task LegacyPlaintextBillingAccountNumber_IsRead_Reported_AndEncryptedOnTheNextWrite()
    {
        const string legacy = "987654321012";
        _factory.Sponsors.Put(new Sponsor
        {
            Id = "legacy-1", TenantId = Tenant, GroupNumber = "GRP-300", EmployerName = "Gamma Co",
            BillingInfo = new BillingInfo { PremiumAmount = 50m, BillingAccountNumber = legacy }
        });

        var read = await BodyOf(await As("finance", ChoRolePermissions.Finance).GetAsync("/api/v1/sponsors/GRP-300"));
        read.Should().NotContain(legacy).And.Contain("1012");
        _factory.Logs.Messages.Should().Contain(m => m.Contains("stored before encryption") && m.Contains("GRP-300") && !m.Contains(legacy));
        _factory.Sponsors.RawByGroup(Tenant, "GRP-300")!.BillingInfo!.BillingAccountNumber.Should().Be(legacy, "a read does not write");

        (await As("enroller", ChoRolePermissions.EnrollmentSpecialist).PutAsJsonAsync("/api/v1/sponsors/GRP-300", new { contactName = "Lee" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = _factory.Sponsors.RawByGroup(Tenant, "GRP-300")!.BillingInfo!.BillingAccountNumber;
        stored.Should().StartWith("enc:v2:");
        (await ScopedSponsorsAsync(r => r.GetByGroupNumberAsync(Tenant, "GRP-300")))!.BillingInfo!.BillingAccountNumber.Should().Be(legacy);
    }

    // ── logs ────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoNumbersInAnyLog_AcrossTheWholeFlow()
    {
        var first = await ProposeAsync();
        await ApproveAsync(As(Proposer, ChoRolePermissions.TenantAdmin), first); // refused: proposer
        await ApproveAsync(FinanceApprover, first);
        var second = await ProposeAsync(body: NachaAccount(Routing2, Account2));
        await FinanceApprover.PostAsJsonAsync($"{Base}/bank-account-changes/{second}/reject", new { reason = "no" });
        await PremiumBilling.GetAsync(FullPath);
        await Service("capitation-service").GetAsync(FullPath);
        await Finance.PostAsync($"{Base}/bank-account-changes",
            new StringContent("""{"eftEnabled":true,"routingNumber":"123456789","accountNumber":"99998888777"}""",
                System.Text.Encoding.UTF8, "application/json"));

        _factory.Logs.Messages.Should().Contain(m => m.Contains("AUDIT sponsor bank-account change proposed"));
        _factory.Logs.Messages.Should().Contain(m => m.Contains("AUDIT sponsor bank-account change approved"));
        _factory.Logs.Messages.Should().Contain(m => m.Contains("AUDIT sponsor bank-account change rejected"));
        NoNumbersLogged(Account2, Routing2, "99998888777", "123456789");
    }

    private async Task<T> ScopedSponsorsAsync<T>(Func<ISponsorRepository, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ISponsorRepository>());
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
