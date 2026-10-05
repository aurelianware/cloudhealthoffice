using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudHealthOffice.FieldProtection;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.NachaTransmission;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Clients;
using PremiumBillingService.Models;
using PremiumBillingService.Repositories;
using PremiumBillingService.Services;
using static PremiumBillingService.Tests.Security.SponsorBankAccountEndToEndTests;

namespace PremiumBillingService.Tests.Security;

/// <summary>
/// NACHA debit files through the real pipeline: the releasing approver gets a
/// masked summary and receipt, the file goes to the bank (a recording stand-in
/// for the SFTP transmitter), and when it cannot be sent it is held encrypted
/// for a platform admin's retrieval or another approver's retry.
/// </summary>
public class NachaTransmissionEndToEndTests : IClassFixture<NachaTransmissionEndToEndTests.Factory>
{
    private const string Tenant = "tenant-1";
    private const string Maker = "finance-user-5";
    private const string Releaser = "approver-9";
    private const string SecondApprover = "approver-2";
    private const string Group = "GRP001";
    private const string Routing = "021000021";
    private const string Account = "000123456789";

    /// <summary>The bank's SFTP drop, in memory.</summary>
    public sealed class StandInBank : INachaTransmitter
    {
        public List<NachaTransmissionRequest> Received { get; } = new();
        public Func<IReadOnlyList<EftDraftStatus>>? DraftStatusesAtSend { get; set; }
        public List<IReadOnlyList<EftDraftStatus>> StatusesSeen { get; } = new();
        public bool Down { get; set; }

        public Task<NachaTransmissionReceipt> TransmitAsync(NachaTransmissionRequest request, CancellationToken cancellationToken = default)
        {
            if (DraftStatusesAtSend != null) StatusesSeen.Add(DraftStatusesAtSend());
            if (Down) throw new NachaTransmissionException("The upload to the bank's SFTP server failed (SshConnectionException).");
            Received.Add(request);
            var facts = NachaFileFacts.From(request.Content);
            return Task.FromResult(new NachaTransmissionReceipt
            {
                TenantId = request.TenantId, FileReference = request.FileReference, RemoteFileName = request.FileName,
                Destination = "sftp://sftp.bank.example:22/inbound", ByteSize = facts.ByteSize, Sha256 = facts.Sha256,
                EntryCount = facts.EntryCount, TotalDebitAmount = facts.TotalDebitAmount, TotalCreditAmount = facts.TotalCreditAmount,
                TransmittedAt = DateTime.UtcNow, TransmittedBy = request.TransmittedBy, RunId = request.RunId, BatchId = request.BatchId,
            });
        }
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        public Mock<IBillingRunRepository> Runs { get; } = new();
        public Mock<IPremiumInvoiceRepository> Invoices { get; } = new();
        public Mock<IEftDraftRepository> Drafts { get; } = new();
        public StandInSponsorService SponsorService { get; } = new();
        public StandInBank Bank { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();
        public InMemoryNachaHeldFileStore Held { get; } = new();

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
                services.AddSingleton(Runs.Object);
                services.AddSingleton(Invoices.Object);
                services.AddSingleton(Drafts.Object);
                services.AddHttpClient(HttpSponsorBankAccountSource.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => SponsorService);
                // The real NachaDispatcher stays; the bank and the store are stand-ins.
                services.RemoveAll<INachaTransmitter>();
                services.AddSingleton<INachaTransmitter>(Bank);
                services.RemoveAll<INachaHeldFileStore>();
                services.AddSingleton<INachaHeldFileStore>(Held);
                services.RemoveAll<IFieldProtector>();
                services.AddSingleton<IFieldProtector>(new DataProtectionFieldProtector(new EphemeralDataProtectionProvider(), "premium-billing-service"));
            });
        }
    }

    private readonly Factory _factory;
    private readonly List<EftDraft> _drafts = new();

    public NachaTransmissionEndToEndTests(Factory factory)
    {
        _factory = factory;
        _factory.Runs.Reset();
        _factory.Invoices.Reset();
        _factory.Drafts.Reset();
        _factory.SponsorService.Reset();
        _factory.Logs.Clear();
        _factory.Bank.Received.Clear();
        _factory.Bank.StatusesSeen.Clear();
        _factory.Bank.Down = false;
        _factory.Bank.DraftStatusesAtSend = () => _drafts.Select(d => d.Status).ToList();

        _factory.Invoices.Setup(r => r.GetByIdAsync("inv-1")).ReturnsAsync(() => new PremiumInvoice
        {
            TenantId = Tenant, Id = "inv-1", InvoiceNumber = "INV-GRP001-2026-03", GroupNumber = Group,
            SponsorName = "Acme Co", BillingRunId = "run-1", CreatedBy = Maker,
            Status = InvoiceStatus.Sent, BalanceDue = 1500m, TotalAmount = 1500m
        });
        _factory.Runs.Setup(r => r.GetByIdAsync("run-1")).ReturnsAsync(() => new BillingRun
        {
            TenantId = Tenant, Id = "run-1", CreatedBy = Maker, ExecutedBy = Maker, InvoiceIds = new List<string> { "inv-1" }
        });
        _factory.Runs.Setup(r => r.UpdateAsync(It.IsAny<BillingRun>())).ReturnsAsync((BillingRun r) => r);
        _factory.Drafts.Setup(r => r.CreateAsync(It.IsAny<EftDraft>()))
            .ReturnsAsync((EftDraft d) => { d.TenantId = Tenant; lock (_drafts) _drafts.Add(d); return d; });
        _factory.Drafts.Setup(r => r.UpdateAsync(It.IsAny<EftDraft>())).ReturnsAsync((EftDraft d) => d);
        _factory.Drafts.Setup(r => r.GetByStatusAsync(It.IsAny<EftDraftStatus>()))
            .ReturnsAsync((EftDraftStatus s) => _drafts.Where(d => d.Status == s).ToList());
        // The conditional Pending-to-Releasing write, as the repositories do it.
        _factory.Drafts.Setup(r => r.TryClaimForReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<string>()))
            .ReturnsAsync((string id, string claim, DateTime at, string by) =>
            {
                lock (_drafts)
                {
                    var d = _drafts.SingleOrDefault(x => x.Id == id && x.Status == EftDraftStatus.Pending);
                    if (d == null) return false;
                    d.Status = EftDraftStatus.Releasing;
                    d.ReleaseClaimId = claim;
                    d.ReleasedBy = by;
                    return true;
                }
            });

        _factory.SponsorService.Accounts[Group] = new
        {
            eftEnabled = true, preferredMethod = "Nacha", routingNumber = Routing, accountNumber = Account,
            accountType = "Checking", accountHolderName = "Acme Co", routingNumberLast4 = "0021", accountNumberLast4 = "6789",
        };
    }

    private HttpClient As(string subject, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", Tenant);
        return client;
    }

    private static void NoNumbersOrFile(string body)
    {
        body.Should().NotContain(Account).And.NotContain(Routing).And.NotContain(Routing[..8])
            .And.NotContainEquivalentOf("fileContent").And.NotContain("enc:v1:")
            .And.NotContain("101 0910"); // a NACHA file header record
    }

    private void NoNumbersLogged()
        => string.Join("\n", _factory.Logs.Messages).Should().NotContain(Account).And.NotContain(Routing[..8]);

    private async Task<(HttpResponseMessage Response, string Body)> ReleaseBatch(string by = Releaser, string role = ChoRolePermissions.FinanceApprover)
    {
        var response = await As(by, role).PostAsJsonAsync("/api/v1/eft/drafts/batch", new { billingRunId = "run-1" });
        return (response, await response.Content.ReadAsStringAsync());
    }

    private async Task<string> HeldFileReference()
    {
        _factory.Bank.Down = true;
        var (response, body) = await ReleaseBatch();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        _factory.Bank.Down = false;
        return json.RootElement.GetProperty("nachaFile").GetProperty("fileReference").GetString()!;
    }

    [Fact]
    public async Task Batch_SendsTheFileToTheBank_TheApproverGetsAMaskedSummaryAndReceipt()
    {
        var (response, body) = await ReleaseBatch();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        NoNumbersOrFile(body);
        using var json = JsonDocument.Parse(body);
        var file = json.RootElement.GetProperty("nachaFile");
        file.GetProperty("transmissionStatus").GetString().Should().Be("Transmitted");
        file.GetProperty("totalDebitAmount").GetDecimal().Should().Be(1500m);
        var entry = file.GetProperty("entries").EnumerateArray().Should().ContainSingle().Subject;
        entry.GetProperty("groupNumber").GetString().Should().Be(Group);
        entry.GetProperty("accountNumberLast4").GetString().Should().Be("6789");
        entry.GetProperty("amount").GetDecimal().Should().Be(1500m);
        var receipt = file.GetProperty("receipt");
        receipt.GetProperty("sha256").GetString().Should().HaveLength(64);
        receipt.GetProperty("entryCount").GetInt32().Should().Be(1);
        receipt.GetProperty("transmittedBy").GetString().Should().Be(Releaser);
        receipt.GetProperty("runId").GetString().Should().Be("run-1");

        // The bank got the full numbers; the person did not.
        var sent = _factory.Bank.Received.Should().ContainSingle().Subject;
        sent.Content.Should().Contain(Account).And.Contain(Routing[..8]);
        sent.TenantId.Should().Be(Tenant);
        NachaFileFacts.From(sent.Content).Sha256.Should().Be(receipt.GetProperty("sha256").GetString());

        // Submitted only after the bank had it; while it was sent the release held them.
        _factory.Bank.StatusesSeen.Single().Should().OnlyContain(s => s == EftDraftStatus.Releasing);
        _drafts.Should().ContainSingle().Which.Status.Should().Be(EftDraftStatus.Submitted);
        NoNumbersLogged();
    }

    [Fact]
    public async Task Generate_ForPendingDrafts_SendsToTheBank_AndMarksSubmittedOnlyAfterSuccess()
    {
        _drafts.Add(new EftDraft
        {
            TenantId = Tenant, Id = "d-1", InvoiceId = "inv-1", GroupNumber = Group, Amount = 900m,
            Method = EftMethod.Nacha, Status = EftDraftStatus.Pending, AccountNumberLast4 = "6789", InitiatedBy = Releaser
        });

        var response = await As(Releaser, ChoRolePermissions.FinanceApprover).PostAsync("/api/v1/eft/nacha/generate", null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        NoNumbersOrFile(body);
        JsonDocument.Parse(body).RootElement.GetProperty("transmissionStatus").GetString().Should().Be("Transmitted");
        _factory.Bank.StatusesSeen.Single().Should().OnlyContain(s => s == EftDraftStatus.Releasing);
        _drafts.Single().Status.Should().Be(EftDraftStatus.Submitted);
        _drafts.Single().NachaFileReference.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BankDown_HoldsTheFileEncrypted_DraftsAwaitRetrieval_NeverSubmitted()
    {
        _factory.Bank.Down = true;

        var (response, body) = await ReleaseBatch();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        NoNumbersOrFile(body);
        using var json = JsonDocument.Parse(body);
        var file = json.RootElement.GetProperty("nachaFile");
        file.GetProperty("transmissionStatus").GetString().Should().Be("AwaitingRetrieval");
        file.GetProperty("transmissionError").GetString().Should().Contain("SFTP");
        var reference = file.GetProperty("fileReference").GetString()!;
        _drafts.Single().Status.Should().Be(EftDraftStatus.AwaitingRetrieval);
        _drafts.Single().ErrorMessage.Should().Contain("platform admin");

        var held = _factory.Held.All.Single(h => h.FileReference == reference);
        held.ProtectedContent.Should().StartWith("enc:v1:").And.NotContain(Account);
        held.ReleasedBy.Should().Be(Releaser);
        held.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));

        var list = await As(Releaser, ChoRolePermissions.FinanceApprover).GetAsync("/api/v1/eft/nacha/held");
        var listBody = await list.Content.ReadAsStringAsync();
        list.StatusCode.Should().Be(HttpStatusCode.OK, listBody);
        listBody.Should().Contain(reference);
        NoNumbersOrFile(listBody);
        NoNumbersLogged();
    }

    [Fact]
    public async Task Retry_ByASecondApprover_Delivers_AndSubmitsTheDrafts()
    {
        var reference = await HeldFileReference();

        var response = await As(SecondApprover, ChoRolePermissions.FinanceApprover)
            .PostAsync($"/api/v1/eft/nacha/held/{reference}/retry", null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        NoNumbersOrFile(body);
        JsonDocument.Parse(body).RootElement.GetProperty("transmissionStatus").GetString().Should().Be("Transmitted");
        _factory.Bank.Received.Should().ContainSingle(r => r.FileReference == reference && r.TransmittedBy == SecondApprover);
        _drafts.Single().Status.Should().Be(EftDraftStatus.Submitted);
    }

    [Fact]
    public async Task Retry_ByTheReleaser_IsRefused()
    {
        var reference = await HeldFileReference();

        var response = await As(Releaser, ChoRolePermissions.FinanceApprover).PostAsync($"/api/v1/eft/nacha/held/{reference}/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Separation of duties");
        _factory.Bank.Received.Should().BeEmpty();
        _drafts.Single().Status.Should().Be(EftDraftStatus.AwaitingRetrieval);
    }

    [Fact]
    public async Task Retry_WithoutPaymentsApprove_IsRefused()
    {
        var reference = await HeldFileReference();

        var response = await As("finance-1", ChoRolePermissions.Finance).PostAsync($"/api/v1/eft/nacha/held/{reference}/retry", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _factory.Bank.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task Retrieve_ByAPlatformAdmin_ReturnsTheFile_IsAudited_AndSubmitsTheDrafts()
    {
        var reference = await HeldFileReference();

        var response = await As("ops-1", ChoRolePermissions.PlatformAdmin)
            .PostAsJsonAsync($"/api/v1/eft/nacha/held/{reference}/retrieve", new { reason = "bank SFTP outage INC-42" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Contain(Account);
        _factory.Logs.Messages.Should().Contain(m => m.Contains("retrieved by platform admin ops-1") && m.Contains("INC-42"));
        _drafts.Single().Status.Should().Be(EftDraftStatus.Submitted);
    }

    [Theory]
    [InlineData("approver-x", ChoRolePermissions.FinanceApprover)]
    [InlineData("admin-1", ChoRolePermissions.TenantAdmin)]
    [InlineData("finance-1", ChoRolePermissions.Finance)]
    public async Task Retrieve_ByAnyoneButAPlatformAdmin_IsRefused(string user, string role)
    {
        var reference = await HeldFileReference();

        var response = await As(user, role).PostAsJsonAsync($"/api/v1/eft/nacha/held/{reference}/retrieve", new { reason = "need it" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NoNumbersOrFile(await response.Content.ReadAsStringAsync());
        _factory.Held.All.Single(h => h.FileReference == reference).Retrievals.Should().BeEmpty();
    }

    [Fact]
    public async Task Retrieve_ByThePlatformAdminWhoReleasedIt_IsRefused()
    {
        _factory.Bank.Down = true;
        var (release, releaseBody) = await ReleaseBatch(by: "ops-releaser", role: ChoRolePermissions.PlatformAdmin);
        release.StatusCode.Should().Be(HttpStatusCode.OK, releaseBody);
        var reference = JsonDocument.Parse(releaseBody).RootElement.GetProperty("nachaFile").GetProperty("fileReference").GetString();

        var response = await As("ops-releaser", ChoRolePermissions.PlatformAdmin)
            .PostAsJsonAsync($"/api/v1/eft/nacha/held/{reference}/retrieve", new { reason = "I released it" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NoNumbersOrFile(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Retrieve_WithoutAReason_Is400()
    {
        var reference = await HeldFileReference();

        var response = await As("ops-1", ChoRolePermissions.PlatformAdmin)
            .PostAsJsonAsync($"/api/v1/eft/nacha/held/{reference}/retrieve", new { reason = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheOldDownloadEndpoint_IsGone()
    {
        var response = await As(Releaser, ChoRolePermissions.FinanceApprover).PostAsync("/api/v1/eft/nacha/generate-and-download", null);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        _factory.Bank.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task NoNachaEndpoint_ReturnsNumbersOrTheFile_ToAnApprover()
    {
        // Every NACHA endpoint an approver can call, in one flow.
        var responses = new List<string>();
        var (_, delivered) = await ReleaseBatch();
        responses.Add(delivered);
        _drafts.Clear();
        var reference = await HeldFileReference();
        responses.Add(await (await As(Releaser, ChoRolePermissions.FinanceApprover).GetAsync("/api/v1/eft/nacha/held")).Content.ReadAsStringAsync());
        responses.Add(await (await As(Releaser, ChoRolePermissions.FinanceApprover).PostAsync($"/api/v1/eft/nacha/held/{reference}/retry", null)).Content.ReadAsStringAsync());
        responses.Add(await (await As(SecondApprover, ChoRolePermissions.FinanceApprover).PostAsync($"/api/v1/eft/nacha/held/{reference}/retry", null)).Content.ReadAsStringAsync());
        responses.Add(await (await As(SecondApprover, ChoRolePermissions.FinanceApprover).PostAsync("/api/v1/eft/nacha/generate", null)).Content.ReadAsStringAsync());
        foreach (var d in _drafts.ToList())
            responses.Add(await (await As(Releaser, ChoRolePermissions.FinanceApprover).GetAsync($"/api/v1/eft/drafts/{d.Id}")).Content.ReadAsStringAsync());

        responses.Should().HaveCountGreaterThan(5);
        foreach (var body in responses)
            NoNumbersOrFile(body);
    }
}
