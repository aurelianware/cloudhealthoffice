using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Support;
using CloudHealthOffice.NachaTransmission;
using CloudHealthOffice.Testing.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace CapitationService.Tests.Services;

/// <summary>
/// Stripe disbursements settle from the signed transfer events (which carry
/// our tenant and disbursement ids); payout events are acknowledged only.
/// Against a real mongod and the real Mongo repositories.
/// </summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class StripeWebhookSettlementTests : IAsyncLifetime
{
    private const string Tenant = "tenant-wh";

    private readonly MongoRunnerFixture _mongo;
    private IMongoDatabase _database = null!;

    public StripeWebhookSettlementTests(MongoRunnerFixture mongo) => _mongo = mongo;

    public Task InitializeAsync()
    {
        _database = _mongo.CreateDatabase("cap_webhook");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _mongo.DropDatabaseAsync(_database);

    private sealed class RequestContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static IHttpContextAccessor TenantRequest()
    {
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        return new RequestContext { HttpContext = http };
    }

    private CapitationStatementRepositoryMongo Statements() => new(_database, TenantRequest(), Mock.Of<ILogger<CapitationStatementRepositoryMongo>>());
    private CapitationDisbursementRepositoryMongo Disbursements() => new(_database, TenantRequest());

    /// <summary>The anonymous webhook request: no tenant until the signed event names one.</summary>
    private async Task DeliverAsync(DisbursementWebhookResult stripeEvent)
    {
        var webhook = new RequestContext { HttpContext = new DefaultHttpContext() };
        var stripe = new Mock<IStripeConnectService>();
        stripe.Setup(s => s.ProcessWebhookAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(stripeEvent);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var service = new CapitationDisbursementService(
            new CapitationDisbursementRepositoryMongo(_database, webhook),
            new CapitationStatementRepositoryMongo(_database, webhook, Mock.Of<ILogger<CapitationStatementRepositoryMongo>>()),
            Mock.Of<ICapitationRunRepository>(),
            new NachaCreditFileService(config, Mock.Of<ILogger<NachaCreditFileService>>()),
            stripe.Object, Mock.Of<IHttpClientFactory>(), config,
            TestSeparationOfDuties.Create(tenantId: Tenant),
            Mock.Of<IProviderBankAccountSource>(), Mock.Of<INachaDispatcher>(), Mock.Of<ILogger<CapitationDisbursementService>>(), webhook);
        await service.ProcessStripeWebhookAsync("{}", "sig");
    }

    private async Task<CapitationDisbursement> SeedAsync(DisbursementStatus status, CapitationStatementStatus statementStatus, string? transferId)
    {
        var disbursement = new CapitationDisbursement
        {
            StatementId = "s-1", StatementNumber = "CAPSTMT-s-1", ProviderNPI = "1234567890", Amount = 500m,
            Method = DisbursementMethod.StripeConnect, Status = status, StripeTransferId = transferId, InitiatedBy = "approver-1", ReleasedBy = "approver-1",
        };
        await Statements().CreateAsync(new CapitationStatement
        {
            Id = "s-1", StatementNumber = "CAPSTMT-s-1", ProviderNPI = "1234567890", Status = statementStatus, NetPayable = 500m,
            EftDisbursementId = disbursement.Id, CreatedBy = "maker-1",
        });
        return await Disbursements().CreateAsync(disbursement);
    }

    private static DisbursementWebhookResult Transfer(string eventType, string transferId, string? disbursementId) => new()
    {
        Handled = true, EventType = eventType, TransferId = transferId, TenantId = Tenant, DisbursementId = disbursementId,
        FailureCode = eventType == "transfer_reversed" ? "TRANSFER_REVERSED" : null,
    };

    [Fact]
    public async Task TransferCreated_SettlesTheDisbursement_AndPaysTheStatement_AndIsIdempotent()
    {
        var d = await SeedAsync(DisbursementStatus.Submitted, CapitationStatementStatus.PaymentInitiated, "tr_1");

        await DeliverAsync(Transfer("transfer_created", "tr_1", d.Id));
        await DeliverAsync(Transfer("transfer_created", "tr_1", d.Id)); // Stripe delivers again

        (await Disbursements().GetByIdAsync(d.Id))!.Status.Should().Be(DisbursementStatus.Settled);
        (await Statements().GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.Paid);
    }

    [Fact]
    public async Task TransferCreated_ForAPaymentUnknownDisbursement_IsFoundByItsId_AndSettlesIt()
    {
        var d = await SeedAsync(DisbursementStatus.PaymentUnknown, CapitationStatementStatus.PaymentUnknown, transferId: null);

        await DeliverAsync(Transfer("transfer_created", "tr_late", d.Id));

        var stored = (await Disbursements().GetByIdAsync(d.Id))!;
        stored.Status.Should().Be(DisbursementStatus.Settled);
        stored.StripeTransferId.Should().Be("tr_late");
        (await Statements().GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.Paid);
    }

    [Fact]
    public async Task TransferCreated_BeforeTheDisbursementIsRecorded_FailsSoStripeRetries()
    {
        await FluentActions.Awaiting(() => DeliverAsync(Transfer("transfer_created", "tr_early", "disb-not-yet")))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TransferReversed_AfterSettlement_ReturnsIt_AndTheSameEventAgainChangesNothing()
    {
        var d = await SeedAsync(DisbursementStatus.Submitted, CapitationStatementStatus.PaymentInitiated, "tr_1");
        await DeliverAsync(Transfer("transfer_created", "tr_1", d.Id));

        await DeliverAsync(Transfer("transfer_reversed", "tr_1", d.Id));
        await DeliverAsync(Transfer("transfer_reversed", "tr_1", d.Id));

        (await Disbursements().GetByIdAsync(d.Id))!.Status.Should().Be(DisbursementStatus.Returned);
        (await Statements().GetByIdAsync("s-1"))!.Status.Should().Be(CapitationStatementStatus.Approved);
    }

    [Theory]
    [InlineData("payout_paid")]
    [InlineData("payout_failed")]
    public async Task PayoutEvents_AreAcknowledged_NotMatchedToDisbursements(string eventType)
    {
        var d = await SeedAsync(DisbursementStatus.Submitted, CapitationStatementStatus.PaymentInitiated, "po_1");

        await DeliverAsync(new DisbursementWebhookResult { Handled = true, EventType = eventType, TransferId = "po_1", TenantId = Tenant });

        (await Disbursements().GetByIdAsync(d.Id))!.Status.Should().Be(DisbursementStatus.Submitted);
    }
}
